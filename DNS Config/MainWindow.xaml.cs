using System.Text;
using System.Windows;
using System;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace DNS_Config
{
	/// <summary>
	/// Interaction logic for MainWindow.xaml
	/// </summary>
	public partial class MainWindow : Window
	{
		private readonly string[] CustomDns = { "83.220.169.155", "212.109.195.93" };
		private readonly string DohTemplate = "https://dns.comss.one/dns-query";

		public MainWindow()
		{
			InitializeComponent();
			LoadInterfaces();
		}

		private void LoadInterfaces()
		{
			var interfaces = DnsManager.GetActiveInterfaces();
			InterfaceComboBox.ItemsSource = interfaces.Select(n => n.Name);
			if (InterfaceComboBox.Items.Count > 0)
				InterfaceComboBox.SelectedIndex = 0;
		}

		private void SetCustomDnsButton_Click(object sender, RoutedEventArgs e)
		{
			if (InterfaceComboBox.SelectedItem is not string name) return;

			bool success = DnsManager.SetDns(name, CustomDns, enableDoh: true, dohTemplate: DohTemplate);
			StatusText.Text = success
				? "Кастомные DNS + DoH установлены!"
				: "Ошибка! Запустите от имени администратора.";
		}

		private void SetDefaultDnsButton_Click(object sender, RoutedEventArgs e)
		{
			if (InterfaceComboBox.SelectedItem is not string name) return;

			bool success = DnsManager.ResetDns(name);
			StatusText.Text = success ? "DNS сброшены на DHCP" : "Ошибка сброса!";
		}
	}
}