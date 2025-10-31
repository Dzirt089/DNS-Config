using System;
using System.Collections.Generic;
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

		// === Провайдеры DoH ===
		public static readonly Dictionary<string, string> DohProviders = new()
		{
			{ "Cloudflare", "https://cloudflare-dns.com/dns-query" },
			{ "Google", "https://dns.google/dns-query" },
			{ "AdGuard", "https://dns.adguard.com/dns-query" },
			{ "Comss.one", "https://dns.comss.one/dns-query" }
		};

		static DnsManager()
		{
			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
			_httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
		}

		public static void Initialize(Action<string> statusCallback)
		{
			StatusUpdate = statusCallback ?? throw new ArgumentNullException(nameof(statusCallback));
		}

		private static void UpdateStatus(string message) => StatusUpdate?.Invoke(message);

		// === Активные интерфейсы ===
		public static NetworkInterface[] GetActiveInterfaces()
		{
			return NetworkInterface.GetAllNetworkInterfaces()
				.Where(n => n.OperationalStatus == OperationalStatus.Up &&
							(n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
							 n.NetworkInterfaceType == NetworkInterfaceType.Ethernet))
				.ToArray();
		}

		// === Универсальная команда ===
		private static async Task<string> RunCommandAsync(string fileName, string args, Encoding encoding)
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
			string output = await process.StandardOutput.ReadToEndAsync();
			string error = await process.StandardError.ReadToEndAsync();
			await Task.Run(() => process.WaitForExit());

			if (process.ExitCode != 0 &&
				!error.Contains("уже запущена") &&
				!error.Contains("already started") &&
				!error.Contains("невозможно") &&
				!error.Contains("2191") &&
				!error.Contains("2182"))
			{
				throw new Exception($"Ошибка: {error}\nКоманда: {args}");
			}

			return output;
		}

		private static Task<string> RunNetshAsync(string args) => RunCommandAsync("netsh.exe", args, Encoding.GetEncoding(866));
		private static Task<string> RunPowerShellAsync(string script) => RunCommandAsync("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"", Encoding.UTF8);

		// === Тест DoH-сервера ===
		public static async Task<bool> TestDohServerAsync(string template)
		{
			try
			{
				string testUrl = $"{template}?dns=AAABAAABAAAAAAAAA3d3dwdleGFtcGxlA2NvbQAAAQAB";
				var response = await _httpClient.GetAsync(testUrl);
				return response.IsSuccessStatusCode &&
					   response.Content.Headers.ContentType?.MediaType?.Contains("dns-message") == true;
			}
			catch { return false; }
		}

		// === Поиск профиля по имени + описанию ===
		private static async Task<string?> GetNetworkProfileGuidAsync(string interfaceName, string description)
		{
			string profilesPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles";
			using var profilesKey = Registry.LocalMachine.OpenSubKey(profilesPath);
			if (profilesKey == null) return null;

			foreach (string guid in profilesKey.GetSubKeyNames())
			{
				using var profileKey = profilesKey.OpenSubKey(guid);
				string? profileName = profileKey?.GetValue("ProfileName") as string;
				string? profileDesc = profileKey?.GetValue("Description") as string;

				if (string.Equals(profileName, interfaceName, StringComparison.OrdinalIgnoreCase))
					return guid;

				if (!string.IsNullOrEmpty(profileDesc) &&
					(profileDesc.Contains(interfaceName, StringComparison.OrdinalIgnoreCase) ||
					 profileDesc.Contains("Ethernet", StringComparison.OrdinalIgnoreCase) ||
					 profileDesc.Contains("Realtek", StringComparison.OrdinalIgnoreCase) ||
					 profileDesc.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
					 profileDesc.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
					profileDesc?.Contains("Сеть", StringComparison.OrdinalIgnoreCase) == true ||
					(profileDesc?.Equals("HUAWEI-1CF69K_5G", StringComparison.OrdinalIgnoreCase) == true && 
					profileName?.Equals(profileDesc, StringComparison.OrdinalIgnoreCase) == true)))
				return guid;
			}
			return null;
		}

		// === Установка DoH через профиль ===
		private static async Task SetDohViaProfileAsync(string interfaceName, bool enable, string? template = null)
		{
			var nic = GetActiveInterfaces().FirstOrDefault(n => n.Name == interfaceName);
			if (nic == null) throw new Exception("Интерфейс не активен.");

			UpdateStatus($"Поиск профиля для '{interfaceName}'...");

			string? profileGuid = await GetNetworkProfileGuidAsync(interfaceName, nic.Description);
			if (profileGuid == null)
			{
				UpdateStatus("Профиль не найден — DoH пропущен (DNS работает)");
				return;
			}

			UpdateStatus($"Профиль найден: {profileGuid.Substring(0, 8)}...");

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
				key.DeleteValue("Enabled", false);
				key.DeleteValue("Template", false);
				key.DeleteValue("AllowFallback", false);
			}
		}

		// === Применение изменений ===
		private static async Task ApplyDnsChangesAsync()
		{
			try { await RunPowerShellAsync("Clear-DnsClientCache"); } catch { }
			try { await RunCommandAsync("ipconfig.exe", "/flushdns", Encoding.GetEncoding(866)); } catch { }
			try { await RunNetshAsync("interface ip delete arpcache"); } catch { }
		}

		// === Установка DNS + DoH ===
		public static async Task<bool> SetDnsAsync(string interfaceName, string[] dnsServers, bool enableDoh, string? dohTemplate)
		{
			try
			{
				var nic = GetActiveInterfaces().FirstOrDefault(n => n.Name == interfaceName);
				if (nic == null) { UpdateStatus("Интерфейс не найден."); return false; }

				string netshName = nic.Name;
				UpdateStatus($"Интерфейс: {netshName}");

				await RunNetshAsync($"interface ip set dns name=\"{netshName}\" source=dhcp");

				if (dnsServers?.Length > 0)
				{
					await RunNetshAsync($"interface ip set dns name=\"{netshName}\" static {dnsServers[0]}");
					for (int i = 1; i < dnsServers.Length && i < 2; i++)
						await RunNetshAsync($"interface ip add dns name=\"{netshName}\" {dnsServers[i]} index={i + 1}");
					UpdateStatus($"DNS: {string.Join(", ", dnsServers)}");
				}

				if (enableDoh && !string.IsNullOrEmpty(dohTemplate))
				{
					var providerName = DohProviders.FirstOrDefault(x => x.Value == dohTemplate).Key ?? "Custom";
					UpdateStatus($"Тест DoH: {providerName}...");

					if (!await TestDohServerAsync(dohTemplate))
					{
						UpdateStatus("DoH-сервер недоступен!");
						return false;
					}

					await SetDohViaProfileAsync(netshName, true, dohTemplate);
					UpdateStatus($"DoH включён: {providerName}");
				}
				else if (enableDoh)
				{
					UpdateStatus("Шаблон пуст.");
					return false;
				}
				else
				{
					await SetDohViaProfileAsync(netshName, false);
					UpdateStatus("DoH отключён");
				}

				await ApplyDnsChangesAsync();
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
		public static async Task ResetDnsAsync(string interfaceName)
		{
			try
			{
				var nic = GetActiveInterfaces().FirstOrDefault(n => n.Name == interfaceName);
				if (nic == null) { UpdateStatus("Интерфейс не найден."); return; }

				await RunNetshAsync($"interface ip set dns name=\"{nic.Name}\" source=dhcp");
				await SetDohViaProfileAsync(nic.Name, false);
				await ApplyDnsChangesAsync();
				UpdateStatus("Сброшено на DHCP");
			}
			catch (Exception ex)
			{
				UpdateStatus($"Ошибка: {ex.Message}");
			}
		}

		// === Проверка текущих DNS ===
		public static async Task<string> GetCurrentDnsStatusAsync(string interfaceName)
		{
			try
			{
				var nic = GetActiveInterfaces().FirstOrDefault(n => n.Name == interfaceName);
				if (nic == null) return "Интерфейс не найден.";

				var ipProps = nic.GetIPProperties();
				var dns = ipProps.DnsAddresses.Select(ip => ip.ToString()).Take(2).ToArray();

				string dohStatus = "Отключено";
				string? guid = await GetNetworkProfileGuidAsync(interfaceName, nic.Description);
				if (guid != null)
				{
					string path = $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles\{guid}\DnsOverHttps";
					using var key = Registry.LocalMachine.OpenSubKey(path);
					if (key?.GetValue("Enabled") is int enabled && enabled == 1)
					{
						string? template = key.GetValue("Template") as string;
						var provider = DohProviders.FirstOrDefault(x => x.Value == template).Key ?? "Custom";
						dohStatus = $"Включено: {provider}";
					}
				}

				return $"Интерфейс: {interfaceName}\n" +
					   $"DNS: {string.Join(", ", dns)}\n" +
					   $"DoH: {dohStatus}\n" +
					   $"Обновлено: {DateTime.Now:HH:mm:ss}";
			}
			catch (Exception ex)
			{
				return $"Ошибка: {ex.Message}";
			}
		}
	}
}