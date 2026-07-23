namespace CarApp.App;

// Achtung: "Application" absichtlich voll qualifiziert — im Projekt existiert der
// Namespace CarApp.Application, der sonst die Namensauflösung stören würde.
public partial class App : Microsoft.Maui.Controls.Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState) =>
        new Window(new MainPage()) { Title = "CarApp" };
}
