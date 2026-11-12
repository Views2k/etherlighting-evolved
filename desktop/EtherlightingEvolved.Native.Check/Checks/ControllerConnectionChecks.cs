using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using EtherlightingEvolved.Native.Pages.Settings;

namespace EtherlightingEvolved.Native.Check.Checks;

internal static class ControllerConnectionChecks
{
    public static void Run(Action<bool, string> check)
    {
        var requests = new List<(string Path, string Method)>();
        var controller = new ControllerConnection((path, method, _) =>
        {
            requests.Add((path, method));
            return Task.FromResult(JsonNode.Parse("""
                {"connected":false,"supported_auth_methods":{
                    "self_hosted":["password"],"unifi_os":["password","api_key"],"cloud_connector":["api_key"]}}
                """));
        }, _ => Task.CompletedTask);
        var window = new Window { Width = 700, Height = 800, Content = new ScrollViewer { Content = controller } };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var fields = controller.GetLogicalDescendants().OfType<SelectBox>().ToList();
            var kind = fields.Single(field => field.Options.Any(option => option.Value == "unifi_os"));
            var method = fields.Single(field => field.Options.Any(option => option.Value == "password"));
            var secrets = controller.GetLogicalDescendants().OfType<SecretField>().ToList();
            SecretField Field(string label) => secrets.Single(field => field.Children.OfType<TextBlock>().Any(title => title.Text == label));
            var password = Field("Controller password");
            var key = Field("API key");

            Choose(kind, "unifi_os");
            check(kind.Value == "unifi_os" && method.IsEnabled && method.Value == "password"
                && password.IsEffectivelyVisible && !key.IsEffectivelyVisible,
                "selecting UniFi OS opens the controller account form and enables the sign-in method");
            Choose(method, "api_key");
            check(method.Value == "api_key" && key.IsEffectivelyVisible && !password.IsEffectivelyVisible,
                "selecting the UniFi OS API key opens its credential field");
            Choose(kind, "self_hosted");
            check(method.Value == "password" && !method.IsEnabled && password.IsEffectivelyVisible,
                "returning to self-hosted Network restores its account form");
            Choose(kind, "unifi_os");
            check(method.Value == "api_key" && key.IsEffectivelyVisible, "returning to UniFi OS retains the draft sign-in method");
            check(requests.SequenceEqual([("/api/controller", "GET")]), "changing controller type and sign-in method only edits the form");
        }
        finally
        {
            controller.Close();
            window.Close();
        }
    }

    static void Choose(SelectBox field, string value)
    {
        field.IsDropDownOpen = true;
        Dispatcher.UIThread.RunJobs();
        var index = field.Options.Select(option => option.Value).ToList().IndexOf(value);
        if (index < 0) throw new InvalidOperationException($"Controller form did not offer {value}.");
        field.SelectedIndex = index;
        field.IsDropDownOpen = false;
        Dispatcher.UIThread.RunJobs();
    }
}
