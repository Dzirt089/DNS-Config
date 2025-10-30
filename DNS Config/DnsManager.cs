using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading.Tasks;

using Microsoft.Win32;

namespace DNS_Config
{
	public static class DnsManager
	{
		private static Action<string>? StatusUpdate;

		private static readonly HttpClient _httpClient = new HttpClient
		{
			Timeout = TimeSpan.FromSeconds(10)
		};

		static DnsManager()
		{
			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
			_httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
		}

		public static void Initialize(Action<string> statusCallback)
		{
			StatusUpdate = statusCallback ?? throw new ArgumentNullException(nameof(statusCallback));
		}

		private static void UpdateStatus(string message)
		{
			StatusUpdate?.Invoke(message);
		}

		// === Получение активных интерфейсов ===
		public static NetworkInterface[] GetActiveInterfaces()
		{
			return NetworkInterface.GetAllNetworkInterfaces()
				.Where(n => n.OperationalStatus == OperationalStatus.Up &&
							(n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
							 n.NetworkInterfaceType == NetworkInterfaceType.Ethernet))
				.ToArray();
		}

		// === Универсальная команда (netsh, net, ipconfig) ===
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

		// === PowerShell (для Clear-DnsClientCache) ===
		private static string RunPowerShell(string script)
		{
			var process = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = "powershell.exe",
					Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					CreateNoWindow = true,
					StandardOutputEncoding = Encoding.UTF8,
					StandardErrorEncoding = Encoding.GetEncoding(866)
				}
			};

			process.Start();
			string output = process.StandardOutput.ReadToEnd();
			string error = process.StandardError.ReadToEnd();
			process.WaitForExit();
			return output; // Игнорируем ошибки
		}

		// === Тест DoH-сервера ===
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

		// === Поиск GUID профиля по MAC-адресу ===
		private static string? GetNetworkProfileGuidByMac(string macAddress)
		{
			string cleanMac = macAddress.Replace("-", "").Replace(":", "").ToUpper();
			string profilesPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles";

			using var profilesKey = Registry.LocalMachine.OpenSubKey(profilesPath);
			if (profilesKey == null) return null;

			foreach (string guid in profilesKey.GetSubKeyNames())
			{
				using var profileKey = profilesKey.OpenSubKey(guid);
				string? managedMac = profileKey?.GetValue("ManagedAddress") as string;
				if (!string.IsNullOrEmpty(managedMac) &&
					managedMac.Replace("-", "").Replace(":", "").ToUpper() == cleanMac)
				{
					return guid;
				}
			}
			return null;
		}

		// === DoH через профиль сети (ОФИЦИАЛЬНЫЙ UI-ПУТЬ) ===
		private static void SetDohViaProfile(string interfaceName, bool enable, string? template = null)
		{
			var nic = NetworkInterface.GetAllNetworkInterfaces()
				.FirstOrDefault(n => n.Name == interfaceName && n.OperationalStatus == OperationalStatus.Up);

			if (nic == null)
				throw new Exception($"Интерфейс '{interfaceName}' не активен.");

			string mac = nic.GetPhysicalAddress().ToString();
			if (string.IsNullOrEmpty(mac))
				throw new Exception("MAC-адрес не найден.");

			string? profileGuid = GetNetworkProfileGuidByMac(mac);
			if (profileGuid == null)
				throw new Exception($"Профиль сети для MAC {mac} не найден. Подключитесь к сети хотя бы раз.");

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

		// === Применение изменений ===
		private static void ApplyDnsChanges()
		{
			try { RunPowerShell("Clear-DnsClientCache"); } catch { }
			try { RunCommand("ipconfig.exe", "/flushdns", Encoding.GetEncoding(866)); } catch { }
			try { RunNetsh("interface ip delete arpcache"); } catch { }
		}

		// === Установка DNS + DoH ===
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

				// 1. Сброс DNS
				RunNetsh($"interface ip set dns name=\"{netshName}\" source=dhcp");

				// 2. Статические DNS
				if (dnsServers?.Length > 0)
				{
					RunNetsh($"interface ip set dns name=\"{netshName}\" static {dnsServers[0]}");
					for (int i = 1; i < dnsServers.Length && i < 2; i++)
						RunNetsh($"interface ip add dns name=\"{netshName}\" {dnsServers[i]} index={i + 1}");
					UpdateStatus($"DNS: {string.Join(", ", dnsServers)}");
				}

				// 3. DoH через профиль сети
				if (enableDoh && !string.IsNullOrEmpty(dohTemplate))
				{
					// Раскомментируй для теста сервера:
					// if (!TestDohServer(dohTemplate))
					// {
					//     UpdateStatus($"DoH-сервер недоступен: {dohTemplate}");
					//     return false;
					// }

					SetDohViaProfile(netshName, true, dohTemplate);
					UpdateStatus($"DoH включён (ручной): {dohTemplate}");
				}
				else if (enableDoh)
				{
					UpdateStatus("Шаблон пуст.");
					return false;
				}
				else
				{
					SetDohViaProfile(netshName, false);
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

		// === Сброс на DHCP ===
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
				SetDohViaProfile(netshName, false);
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