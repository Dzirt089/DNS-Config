using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;

namespace DNS_Config
{
	public partial class MainWindow : Window
	{
		private readonly string[] CustomDns = { "83.220.169.155", "212.109.195.93" };
		private readonly Storyboard _spinnerStoryboard;

		public MainWindow()
		{
			InitializeComponent();
			DnsManager.Initialize(s => Dispatcher.Invoke(() => StatusText.Text = s));
			LoadInterfaces();
			DohProviderComboBox.SelectedIndex = 3;

			// Правильное имя
			_spinnerStoryboard = (Storyboard)FindResource("SpinnerAnimation");
		}

		private void LoadInterfaces()
		{
			var interfaces = DnsManager.GetActiveInterfaces();
			InterfaceComboBox.ItemsSource = interfaces.Select(n => n.Name);
			if (InterfaceComboBox.Items.Count > 0)
				InterfaceComboBox.SelectedIndex = 0;
		}

		private async void SetCustomDnsButton_Click(object sender, RoutedEventArgs e)
		{
			if (InterfaceComboBox.SelectedItem is not string name) return;
			if (DohProviderComboBox.SelectedValue is not string template) return;

			await RunWithSpinner(async () => await DnsManager.SetDnsAsync(name, CustomDns, true, template));
		}

		private async void SetDefaultDnsButton_Click(object sender, RoutedEventArgs e)
		{
			if (InterfaceComboBox.SelectedItem is not string name) return;
			await RunWithSpinner(async () => await DnsManager.ResetDnsAsync(name));
		}

		private async void CheckDnsButton_Click(object sender, RoutedEventArgs e)
		{
			if (InterfaceComboBox.SelectedItem is not string name) return;
			await RunWithSpinner(async () =>
			{
				string status = await DnsManager.GetCurrentDnsStatusAsync(name);
				StatusText.Text = status;
			});
		}

		private async Task RunWithSpinner(Func<Task> action)
		{
			SpinnerOverlay.Visibility = Visibility.Visible;
			_spinnerStoryboard.Begin();

			try
			{
				await action();
			}
			catch (Exception ex)
			{
				StatusText.Text = $"Ошибка: {ex.Message}";
			}
			finally
			{
				_spinnerStoryboard.Stop();
				SpinnerOverlay.Visibility = Visibility.Collapsed;
			}
		}
	}
}