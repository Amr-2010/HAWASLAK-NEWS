using Microsoft.Maui.Controls;

namespace Hawaslak.Mobile;

public class App : Application
{
    public App()
    {
        var webView = new WebView
        {
            Source = "https://hawaslak-news.onrender.com",
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill
        };
        MainPage = new ContentPage
        {
            Title = "هيوصلك نيوز",
            BackgroundColor = Colors.White,
            Content = webView
        };
    }
}
