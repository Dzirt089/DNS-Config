using System.Windows;

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
			DnsManager.Initialize(s => StatusText.Text = s); // ← UI-обновление
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
			DnsManager.SetDns(name, CustomDns, enableDoh: true, dohTemplate: DohTemplate);
		}

		private void SetDefaultDnsButton_Click(object sender, RoutedEventArgs e)
		{
			if (InterfaceComboBox.SelectedItem is not string name) return;
			DnsManager.ResetDns(name);
		}
	}
}