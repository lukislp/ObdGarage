namespace CarApp.App;

// Note: "Application" is deliberately fully qualified — the project has a
// CarApp.Application namespace that would otherwise interfere with name resolution.
public partial class App : Microsoft.Maui.Controls.Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState) =>
        new Window(new MainPage()) { Title = "CarApp" };
}
