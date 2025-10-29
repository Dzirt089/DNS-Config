using Microsoft.Win32;

using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;

namespace DNS_Config
{
	public static class DnsManager
	{
		private static Action<string>? StatusUpdate;

		static DnsManager()
		{
			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
			_httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
		}

		public static void Initialize(Action<string> statusCallback)
		{
			StatusUpdate = statusCallback ?? throw new ArgumentNullException(nameof(statusCallback));
		}
		private static string? GetNetworkProfileGuid(string interfaceName)
		{
			string profilesPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles";
			using var profilesKey = Registry.LocalMachine.OpenSubKey(profilesPath);
			if (profilesKey == null) return null;

			foreach (string guid in profilesKey.GetSubKeyNames())
			{
				using var profileKey = profilesKey.OpenSubKey(guid);
				string? profileName = profileKey?.GetValue("ProfileName") as string;
				string? description = profileKey?.GetValue("Description") as string;

				// Ищем по ProfileName или Description
				if (profileName == interfaceName || description == interfaceName)
					return guid;
			}
			return null;
		}
		private static void SetDohViaProfile(string interfaceName, bool enable, string? template = null)
		{
			// 1. Находим GUID профиля по имени интерфейса
			string? profileGuid = GetNetworkProfileGuid(interfaceName);
			if (profileGuid == null)
				throw new Exception($"Профиль сети для '{interfaceName}' не найден.");

			string path = $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles\{profileGuid}\DnsOverHttps";

			using var key = Registry.LocalMachine.CreateSubKey(path);
			if (enable && !string.IsNullOrEmpty(template))
			{
				key.SetValue("Enabled", 1, RegistryValueKind.DWord);
				key.SetValue("Template", template, RegistryValueKind.String);
				key.SetValue("AllowFallback", 0, RegistryValueKind.DWord);
			}
			else
			{
				key.DeleteValue("Enabled", throwOnMissingValue: false);
				key.DeleteValue("Template", throwOnMissingValue: false);
				key.DeleteValue("AllowFallback", throwOnMissingValue: false);
			}
		}
		private static void UpdateStatus(string message)
		{
			StatusUpdate?.Invoke(message);
		}

		public static NetworkInterface[] GetActiveInterfaces()
		{
			return NetworkInterface.GetAllNetworkInterfaces()
				.Where(n => n.OperationalStatus == OperationalStatus.Up &&
							(n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
							 n.NetworkInterfaceType == NetworkInterfaceType.Ethernet))
				.ToArray();
		}

		private static string RunCommand(string fileName, string args, Encoding encoding)
		{
			var process = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = fileName,
					Arguments = args,
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					CreateNoWindow = true,
					StandardOutputEncoding = encoding,
					StandardErrorEncoding = encoding
				}
			};

			process.Start();
			string output = process.StandardOutput.ReadToEnd();
			string error = process.StandardError.ReadToEnd();
			process.WaitForExit();

			if (process.ExitCode != 0)
			{
				if (error.Contains("уже запущена") || error.Contains("already started") ||
					error.Contains("невозможно") || error.Contains("2191") || error.Contains("2182"))
				{
					return output;
				}

				throw new Exception($"{fileName} ошибка (код {process.ExitCode}):\nКоманда: {args}\nОшибка: {error}\nВывод: {output}");
			}

			return output;
		}

		private static string RunNetsh(string args) => RunCommand("netsh.exe", args, Encoding.GetEncoding(866));
		private static string RunNet(string args) => RunCommand("net.exe", args, Encoding.GetEncoding(866));

		private static readonly HttpClient _httpClient = new HttpClient
		{
			Timeout = TimeSpan.FromSeconds(10)
		};


		private static async Task<bool> TestDohServerAsync(string template)
		{
			try
			{
				string testUrl = $"{template}?dns=AAABAAABAAAAAAAAA3d3dwdleGFtcGxlA2NvbQAAAQAB";
				var response = await _httpClient.GetAsync(testUrl);
				return response.IsSuccessStatusCode &&
					   response.Content.Headers.ContentType?.MediaType?.Contains("dns-message") == true;
			}
			catch
			{
				return false;
			}
		}

		private static bool TestDohServer(string template)
		{
			return Task.Run(() => TestDohServerAsync(template)).GetAwaiter().GetResult();
		}

		private static void SetDohViaRegistry(string interfaceName, bool enable, string? template = null)
		{
			string path = $@"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters\DohInterfaceSettings\{interfaceName}";

			using var key = Registry.LocalMachine.CreateSubKey(path);
			if (enable && !string.IsNullOrEmpty(template))
			{
				key.SetValue("Enabled", 1, RegistryValueKind.DWord);
				key.SetValue("ServerTemplate", template, RegistryValueKind.String);
				key.SetValue("FallbackAllowed", 0, RegistryValueKind.DWord);
			}
			else
			{
				key.DeleteValue("Enabled", throwOnMissingValue: false);
				key.DeleteValue("ServerTemplate", throwOnMissingValue: false);
				key.DeleteValue("FallbackAllowed", throwOnMissingValue: false);
			}
		}

		private static void ApplyDnsChanges()
		{
			try { RunCommand("ipconfig.exe", "/flushdns", Encoding.GetEncoding(866)); } catch { }
			try { RunNetsh("interface ip delete arpcache"); } catch { }
		}

		public static bool SetDns(string interfaceName, string[] dnsServers, bool enableDoh = false, string? dohTemplate = null)
		{
			try
			{
				var nic = GetActiveInterfaces().FirstOrDefault(n => n.Name == interfaceName);
				if (nic == null)
				{
					UpdateStatus("Интерфейс не найден.");
					return false;
				}

				string netshName = nic.Name;
				UpdateStatus($"Интерфейс: {netshName}");

				RunNetsh($"interface ip set dns name=\"{netshName}\" source=dhcp");

				if (dnsServers?.Length > 0)
				{
					RunNetsh($"interface ip set dns name=\"{netshName}\" static {dnsServers[0]}");
					for (int i = 1; i < dnsServers.Length && i < 2; i++)
						RunNetsh($"interface ip add dns name=\"{netshName}\" {dnsServers[i]} index={i + 1}");
					UpdateStatus($"DNS: {string.Join(", ", dnsServers)}");
				}

				if (enableDoh && !string.IsNullOrEmpty(dohTemplate))
				{
					//if (!TestDohServer(dohTemplate))
					//{
					//	UpdateStatus($"DoH-сервер недоступен: {dohTemplate}");
					//	return false;
					//}
					SetDohViaRegistry(netshName, true, dohTemplate);
					UpdateStatus($"DoH: {dohTemplate}");
				}
				else if (enableDoh)
				{
					UpdateStatus("DoH включён, но шаблон пуст.");
					return false;
				}
				else
				{
					SetDohViaRegistry(netshName, false);
					UpdateStatus("DoH отключён");
				}

				ApplyDnsChanges();
				UpdateStatus("Готово!");
				return true;
			}
			catch (Exception ex)
			{
				UpdateStatus($"Ошибка: {ex.Message}");
				return false;
			}
		}

		public static bool ResetDns(string interfaceName)
		{
			try
			{
				var nic = GetActiveInterfaces().FirstOrDefault(n => n.Name == interfaceName);
				if (nic == null)
				{
					UpdateStatus("Интерфейс не найден.");
					return false;
				}

				string netshName = nic.Name;
				RunNetsh($"interface ip set dns name=\"{netshName}\" source=dhcp");
				SetDohViaRegistry(netshName, false);
				ApplyDnsChanges();
				UpdateStatus("Сброшено на DHCP");
				return true;
			}
			catch (Exception ex)
			{
				UpdateStatus($"Ошибка: {ex.Message}");
				return false;
			}
		}
	}
}