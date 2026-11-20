using Microsoft.Maui;
using Microsoft.UI.Xaml;
namespace Hawaslak.Mobile.WinUI;
public partial class App : MauiWinUIApplication
{
    public App() { InitializeComponent(); }
    protected override MauiApp CreateMauiApp() => Hawaslak.Mobile.MauiProgram.CreateMauiApp();
}
