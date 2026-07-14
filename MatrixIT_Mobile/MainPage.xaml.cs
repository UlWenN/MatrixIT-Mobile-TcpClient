namespace MatrixIT_Mobile;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();

        BindingContext = new TerminalViewModel();
    }
}