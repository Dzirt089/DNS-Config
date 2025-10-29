using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;

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
					Verb = "runas"
				}
			};

			process.Start();
			string output = process.StandardOutput.ReadToEnd();
			string error = process.StandardError.ReadToEnd();
			process.WaitForExit();

			return process.ExitCode == 0 ? output : throw new Exception(error ?? "Неизвестная ошибка netsh");
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
				// Проверяем, существует ли интерфейс
				var nic = GetActiveInterfaces().FirstOrDefault(n => n.Name == interfaceName);
				if (nic == null) return false;

				string alias = nic.Description; // netsh использует Description, а не Name!

				// Сбрасываем старые настройки
				RunNetsh($"interface ip set dns name=\"{alias}\" source=dhcp");

				if (dnsServers != null && dnsServers.Length > 0)
				{
					// Устанавливаем статические DNS
					RunNetsh($"interface ip set dns name=\"{alias}\" static {dnsServers[0]}");
					for (int i = 1; i < dnsServers.Length && i < 2; i++)
					{
						RunNetsh($"interface ip add dns name=\"{alias}\" {dnsServers[i]} index={i + 1}");
					}
				}

				// DoH (только если включено)
				if (enableDoh && !string.IsNullOrEmpty(dohTemplate))
				{
					RunNetsh($"interface ip set dns name=\"{alias}\" dhcp");
					RunNetsh($"dns client set dohpolicy interface=\"{alias}\" policy=3 template=\"{dohTemplate}\"");
				}
				else if (enableDoh)
				{
					return false; // DoH без шаблона невозможен
				}
				else
				{
					RunNetsh($"dns client set dohpolicy interface=\"{alias}\" policy=1"); // Отключаем DoH
				}

				FlushDns();
				return true;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Ошибка: {ex.Message}");
				return false;
			}
		}

		public static bool ResetDns(string interfaceName)
		{
			try
			{
				var nic = GetActiveInterfaces().FirstOrDefault(n => n.Name == interfaceName);
				if (nic == null) return false;

				string alias = nic.Description;

				RunNetsh($"interface ip set dns name=\"{alias}\" source=dhcp");
				RunNetsh($"dns client set dohpolicy interface=\"{alias}\" policy=1");

				FlushDns();
				return true;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Ошибка: {ex.Message}");
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
