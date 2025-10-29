using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Windows;

namespace DNS_Config
{
	public static class DnsManager
	{
		private static string RunNetsh(string args)
		{
			var process = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = "netsh.exe",
					Arguments = args,
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					CreateNoWindow = true,
					// CP866 теперь доступна!
					StandardOutputEncoding = Encoding.GetEncoding(866),
					StandardErrorEncoding = Encoding.GetEncoding(866)
				}
			};

			process.Start();
			string output = process.StandardOutput.ReadToEnd();
			string error = process.StandardError.ReadToEnd();
			process.WaitForExit();

			if (process.ExitCode != 0)
			{
				string fullError = $"netsh ошибка (код {process.ExitCode}):\n" +
								   $"Команда: {args}\n" +
								   $"Ошибка: {error.Trim()}\n" +
								   $"Вывод: {output.Trim()}";
				throw new Exception(fullError);
			}

			return output;
		}

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
					StandardOutputEncoding = System.Text.Encoding.UTF8,  // ← UTF-8
					StandardErrorEncoding = System.Text.Encoding.UTF8    // ← UTF-8
				}
			};

			process.Start();
			string output = process.StandardOutput.ReadToEnd();
			string error = process.StandardError.ReadToEnd();
			process.WaitForExit();

			if (process.ExitCode != 0)
			{
				throw new Exception($"PowerShell ошибка:\n{error}\nВывод: {output}");
			}

			return output;
		}

		public static NetworkInterface[] GetActiveInterfaces()
		{
			return NetworkInterface.GetAllNetworkInterfaces()
				.Where(n => n.OperationalStatus == OperationalStatus.Up &&
							(n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
							 n.NetworkInterfaceType == NetworkInterfaceType.Ethernet))
				.ToArray();
		}

		public static bool SetDns(string interfaceName, string[] dnsServers, bool enableDoh = false, string dohTemplate = null)
		{
			try
			{
				var nic = GetActiveInterfaces().FirstOrDefault(n => n.Name == interfaceName);
				if (nic == null) return false;

				string netshName = nic.Name;

				// 1. Сброс DNS
				RunNetsh($"interface ip set dns name=\"{netshName}\" source=dhcp");

				// 2. Установка статических DNS
				if (dnsServers != null && dnsServers.Length > 0)
				{
					RunNetsh($"interface ip set dns name=\"{netshName}\" static {dnsServers[0]}");
					for (int i = 1; i < dnsServers.Length && i < 2; i++)
					{
						RunNetsh($"interface ip add dns name=\"{netshName}\" {dnsServers[i]} index={i + 1}");
					}
				}

				// 3. DoH через PowerShell
				if (enableDoh && !string.IsNullOrEmpty(dohTemplate))
				{
					string psScript = $@"
                    Set-DnsClientDohPolicy -InterfaceAlias '{netshName}' -Policy 3 -Template '{dohTemplate}' -AllowFallbackToUdp:$false;
                    Write-Output 'DoH включён';
                ";
					RunPowerShell(psScript);
				}
				else if (enableDoh)
				{
					return false;
				}
				else
				{
					string psScript = $"Set-DnsClientDohPolicy -InterfaceAlias '{netshName}' -Policy 1";
					RunPowerShell(psScript);
				}

				FlushDns();
				return true;
			}
			catch (Exception ex)
			{
				MessageBox.Show($"Ошибка: {ex.Message}");
				return false;
			}
		}

		public static bool ResetDns(string interfaceName)
		{
			try
			{
				var nic = GetActiveInterfaces().FirstOrDefault(n => n.Name == interfaceName);
				if (nic == null) return false;

				string netshName = nic.Name;

				RunNetsh($"interface ip set dns name=\"{netshName}\" source=dhcp");
				RunPowerShell($"Set-DnsClientDohPolicy -InterfaceAlias '{netshName}' -Policy 1");

				FlushDns();
				return true;
			}
			catch (Exception ex)
			{
				MessageBox.Show($"Ошибка: {ex.Message}");
				return false;
			}
		}

		private static void FlushDns()
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = "ipconfig",
				Arguments = "/flushdns",
				UseShellExecute = false,
				CreateNoWindow = true
			})?.WaitForExit();
		}
	}
}
