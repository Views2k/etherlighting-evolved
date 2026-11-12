using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EtherlightingEvolved.Native.Core;
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
        RememberLogin(check, modern: true, existing: true);
        RememberLogin(check, modern: true, existing: false);
        RememberLogin(check, modern: false, existing: true);
    }

    static void RememberLogin(Action<bool, string> check, bool modern, bool existing)
    {
        const string url = "https://console.example.test";
        var descriptor = new JsonObject { ["url"] = url, ["controller_kind"] = "unifi_os", ["auth_method"] = "api_key", ["console_id"] = null };
        var status = JsonNode.Parse("""
            {"connected":false,"configured":false,"preferences_supported":true,"supported_auth_methods":{
              "self_hosted":["password"],"unifi_os":["password","api_key"],"cloud_connector":["api_key"]}}
            """)!.AsObject();
        status["auto_reconnect"] = existing;
        status["saved_login_available"] = existing;
        status["saved_login_matches_connection"] = false;
        if (modern)
        {
            status["remember_on_connect_supported"] = true;
            status["saved_target"] = existing ? descriptor.DeepClone() : null;
        }
        var attempts = new List<JsonObject>();
        var saves = 0;
        var controller = new ControllerConnection((path, verb, body) =>
        {
            if (path == "/api/controller/connect" && verb == "POST")
            {
                attempts.Add(body!.AsObject());
                if (modern && !existing && attempts.Count == 1)
                    throw new ApiException("", "certificate_untrusted", 409, false,
                        new JsonObject { ["certificate_sha256"] = new string('a', 64) });
                if (modern && !existing && attempts.Count == 2)
                {
                    status["pending_site_choice"] = descriptor.DeepClone();
                    status["pending_site_choice"]!["sites"] = JsonNode.Parse("""[{"id":"first","name":"First"},{"id":"second","name":"Second"}]""");
                }
                else
                {
                    foreach (var (name, value) in descriptor) status[name] = value?.DeepClone();
                    status["connected"] = true;
                    status["configured"] = true;
                    status["can_save_login"] = true;
                    status["pending_site_choice"] = null;
                    status["saved_login_matches_connection"] = modern && existing;
                    if (modern && !existing) status["saved_login_error"] = new JsonObject { ["code"] = "controller_login_save_failed" };
                }
            }
            else if (path == "/api/controller/preferences" && verb == "PUT")
            {
                ++saves;
                status["saved_login_error"] = null;
                status["auto_reconnect"] = true;
                status["saved_login_available"] = true;
                status["saved_login_matches_connection"] = true;
            }
            else if (path != "/api/controller" || verb != "GET") throw new InvalidOperationException("Unexpected controller request.");
            return Task.FromResult<JsonNode?>(status.DeepClone());
        }, _ => Task.CompletedTask);
        var window = new Window { Width = 700, Height = 900, Content = new ScrollViewer { Content = controller } };
        IEnumerable<T> Controls<T>() where T : Control => controller.GetLogicalDescendants().OfType<T>();
        void Click(string title)
        {
            var button = Controls<Button>().Single(item => Equals(item.Content, title) && item.IsEffectivelyEnabled && item.IsEffectivelyVisible);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var kind = Controls<SelectBox>().Single(field => field.Options.Any(option => option.Value == "unifi_os"));
            var method = Controls<SelectBox>().Single(field => field.Options.Any(option => option.Value == "password"));
            var address = Controls<TextBox>().Single(input => input.MaxLength == 2048);
            var key = Controls<SecretField>().Single(field => field.Input.MaxLength == 4096).Input;
            var checkbox = Controls<CheckRow>().Single().Box;
            check(checkbox.IsChecked == existing, "reconnect starts with the saved opt-in");
            if (modern && existing)
            {
                check(kind.Value == "unifi_os" && method.Value == "api_key" && address.Text == url && string.IsNullOrEmpty(key.Text),
                    "a disconnected saved controller reopens with its address and API key method, keeping the key private");
                Click("Connect and save login");
                check(attempts.Count == 0, "a saved target description alone cannot submit an empty API key");
            }
            else
            {
                Choose(kind, "unifi_os");
                Choose(method, "api_key");
                address.Text = url;
            }
            key.Text = "fixture-key";
            Dispatcher.UIThread.RunJobs();
            if (modern && existing)
            {
                var changed = status.DeepClone().AsObject();
                changed["saved_target"]!["auth_method"] = "password";
                controller.Update(changed);
                check(method.Value == "api_key" && key.Text == "fixture-key", "live status preserves an edited sign-in draft");
            }
            if (!existing)
            {
                checkbox.BringIntoView();
                Dispatcher.UIThread.RunJobs();
                var point = checkbox.TranslatePoint(new Point(6, checkbox.Bounds.Height / 2), window)!.Value;
                window.MouseMove(point);
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                check(checkbox.IsChecked == true, "clicking reconnect opts in before the first controller connection");
            }
            Click(modern ? "Connect and save login" : "Connect controller");
            if (modern && !existing)
            {
                Click("Trust this certificate and connect");
                var site = Controls<SelectBox>().Single(field => field.Options.Any(option => option.Value == "second"));
                Choose(site, "second");
                check(saves == 0 && attempts.Count == 2 && attempts[1].Str("certificate_sha256") == new string('a', 64),
                    "trust and site selection keep the save intent pending");
                Click("Connect and save login");
                check(attempts.Last().Str("site") == "second" && string.IsNullOrEmpty(key.Text) && saves == 0
                    && Controls<NoticeBox>().Any(notice => notice.IsError && notice.Text.Contains("retry saving")),
                    "a protected save failure keeps the controller connected and offers a retry");
                Click("Save reconnect preference");
                check(attempts.Count == 3 && saves == 1 && Controls<NoticeBox>().Any(notice => notice.Text.Contains("Controller login saved")),
                    "retrying saves the established login without another connection or key entry");
            }
            else if (modern)
                check(saves == 0 && Controls<NoticeBox>().Any(notice => notice.Text == "Controller connected and login saved."),
                    "confirmed connect-and-save updates the saved login with one request");
            check(attempts.Count > 0 && attempts.All(body => modern ? body.Bool("remember") == true : !body.ContainsKey("remember")),
                modern ? "connect carries the selected save intent through every step" : "legacy servers receive their original connect payload");
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
