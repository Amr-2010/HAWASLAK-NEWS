using Microsoft.Maui.Controls;
namespace Hawaslak.Mobile;
public class App : Application { public App() { MainPage = new ContentPage { Title="Hawaslak News", Content = new VerticalStackLayout { Padding=30, Children={ new Label { Text="هوصلك نيوز | Hawaslak News", FontSize=26 }, new Label { Text="Mobile client scaffold — API integration pending" } } } }; } }
