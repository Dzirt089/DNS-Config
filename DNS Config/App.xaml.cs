using System.Configuration;
using System.Data;
using System.Text;
using System.Windows;

namespace DNS_Config
{
	/// <summary>
	/// Interaction logic for App.xaml
	/// </summary>
	// В App.xaml.cs (или Program.cs для .NET 6+)
	public partial class App : Application
	{
		protected override void OnStartup(StartupEventArgs e)
		{
			// РЕГИСТРИРУЕМ CP866 и другие OEM-кодировки
			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

			base.OnStartup(e);
		}
	}

}
