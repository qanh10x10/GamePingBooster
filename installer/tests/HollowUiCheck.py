import sys
from pathlib import Path

def run_checks():
    repo_root = Path(__file__).resolve().parent.parent.parent
    print(f"Checking repo root: {repo_root}")

    # 1. MainWindow.axaml checks
    main_window_file = repo_root / "client" / "src" / "GamePingBooster.App" / "Views" / "MainWindow.axaml"
    assert main_window_file.exists(), f"MainWindow.axaml missing: {main_window_file}"
    main_window_text = main_window_file.read_text(encoding="utf-8")

    # Dimensions check
    assert 'Width="420"' in main_window_text and 'Height="620"' in main_window_text, "Window dimensions must be 420x620"
    assert 'MinWidth="380"' in main_window_text and 'MinHeight="580"' in main_window_text, "Window min dimensions must be 380x580"

    # Header visible title check
    assert "main.title" in main_window_text or "Hollow Ping Booster" in main_window_text, (
        "MainWindow header must contain visible heading"
    )
    assert '<TextBlock Grid.Column="0" Text="{DynamicResource main.title}"' in main_window_text, (
        "MainWindow header Grid.Row=0 Column 0 must display {DynamicResource main.title}"
    )

    # ToolTip binding on Scope row
    assert 'ToolTip.Tip="{Binding ScopeTip}"' in main_window_text, (
        "MainWindow Scope row must bind ToolTip.Tip to ScopeTip"
    )

    # Built-in server is a visible selectable option even when it is the only real relay.
    main_vm_file = repo_root / "client" / "src" / "GamePingBooster.App" / "ViewModels" / "MainViewModel.cs"
    assert main_vm_file.exists(), f"MainViewModel.cs missing: {main_vm_file}"
    main_vm_text = main_vm_file.read_text(encoding="utf-8")
    assert "public bool ShowRelayChoice => RelayItems.Count > 1;" in main_vm_text, (
        "Server picker must show with Automatic plus one built-in server"
    )

    # Singapore West must never be presented as a custom relay setting.
    settings_file = repo_root / "client" / "src" / "GamePingBooster.App" / "Views" / "SettingsWindow.axaml"
    assert settings_file.exists(), f"SettingsWindow.axaml missing: {settings_file}"
    settings_text = settings_file.read_text(encoding="utf-8")
    assert "relay.example.com:51820" in settings_text, (
        "Custom relay field must use a neutral example endpoint"
    )
    assert "ping.hollow-agent.xyz:51820" not in settings_text, (
        "Built-in Singapore West endpoint must not appear in custom relay settings"
    )
    assert 'Text="{Binding HollowPsk}"' in settings_text, (
        "Settings must expose a separate masked Hollow VPS key field"
    )

    settings_vm_file = repo_root / "client" / "src" / "GamePingBooster.App" / "ViewModels" / "SettingsViewModel.cs"
    settings_vm_text = settings_vm_file.read_text(encoding="utf-8")
    assert "public string HollowPsk" in settings_vm_text, (
        "SettingsViewModel must keep the built-in Hollow key separate from custom Psk"
    )
    settings_window_cs = repo_root / "client" / "src" / "GamePingBooster.App" / "Views" / "MainWindow.axaml.cs"
    settings_window_text = settings_window_cs.read_text(encoding="utf-8")
    assert "vm.HasHollowKey" in settings_window_text, (
        "Settings window must receive built-in key presence without receiving the key"
    )

    # Built-in DNS is lazy: no startup/profile-load resolver; connect resolves the selected endpoint.
    tunnel_file = repo_root / "client" / "src" / "GamePingBooster.Service" / "Tunnel" / "TunnelEngine.cs"
    tunnel_text = tunnel_file.read_text(encoding="utf-8")
    assert "ResolveEndpointAsync(builtin.Endpoint" in tunnel_text, (
        "Built-in endpoint must resolve at connect time"
    )
    assert "RelayEndpoints.Count == 0" in tunnel_text, (
        "Built-in selection must remain independent of custom relayEndpoints"
    )
    assert "var otherRelayIps = FailoverOrder(_relay)" in tunnel_text, (
        "Connect route pins must use the selected relay's trust domain"
    )
    assert "var otherRelayIps = FailoverOrder(relay)" in tunnel_text, (
        "Reconnect route pins must use the selected relay's trust domain"
    )
    ping_round = tunnel_text.split("private async Task<List<RelayOption>> PingRoundAsync()", 1)[1].split(
        "public List<RelayOption> RelayOptions()", 1)[0]
    assert "else if (!relay.Id.Equals(ServiceConfig.BuiltInRelayId, StringComparison.OrdinalIgnoreCase))" in ping_round, (
        "Refreshing the picker must not resolve the unselected built-in hostname"
    )
    can_save = settings_vm_text.split("public bool CanSave =>", 1)[1].split(";", 1)[0]
    assert "_alreadyHollowConfigured" in can_save, (
        "Saved built-in-only settings must remain saveable with an empty write-only key box"
    )

    # 2. MainViewModel.cs checks
    vm_file = repo_root / "client" / "src" / "GamePingBooster.App" / "ViewModels" / "MainViewModel.cs"
    assert vm_file.exists(), f"MainViewModel.cs missing: {vm_file}"
    vm_text = vm_file.read_text(encoding="utf-8")

    assert "public string ScopeTip" in vm_text, "MainViewModel must declare ScopeTip property"
    assert '"fullTunnel.policy.tip"' in vm_text, "ScopeTip must reference fullTunnel.policy.tip"
    assert '"fullTunnel.routes"' in vm_text, "RouteText must reference fullTunnel.routes for FullTunnel mode"

    # 3. StringsEn.cs checks
    en_file = repo_root / "client" / "src" / "GamePingBooster.App" / "Services" / "Localization" / "StringsEn.cs"
    assert en_file.exists(), f"StringsEn.cs missing: {en_file}"
    en_text = en_file.read_text(encoding="utf-8")

    assert '["main.title"] = "Hollow Ping Booster"' in en_text, "StringsEn must define main.title"
    assert '["fullTunnel.apps"] = "All games and apps (IPv4)"' in en_text, (
        "StringsEn fullTunnel.apps must clearly label IPv4 mode"
    )
    assert '["fullTunnel.policy.tip"]' in en_text, "StringsEn must define fullTunnel.policy.tip"
    assert "no kill switch" in en_text.lower(), "fullTunnel policy in StringsEn must state no kill switch"
    assert "direct" in en_text.lower(), "fullTunnel policy in StringsEn must explain traffic returns direct"
    assert '["fullTunnel.routes"]' in en_text, "StringsEn must define fullTunnel.routes"

    # Must NOT claim DNS/IPv6 secured from configuration alone
    assert '["fullTunnel.hint"] = "Full tunnel: all Internet traffic uses your VPS. IPv6 is blocked while connected."' not in en_text, (
        "StringsEn must not claim IPv6 is blocked without qualification"
    )

    # 4. StringsVi.cs checks
    vi_file = repo_root / "client" / "src" / "GamePingBooster.App" / "Services" / "Localization" / "StringsVi.cs"
    assert vi_file.exists(), f"StringsVi.cs missing: {vi_file}"
    vi_text = vi_file.read_text(encoding="utf-8")

    assert '["main.title"] = "Hollow Ping Booster"' in vi_text, "StringsVi must define main.title"
    assert '["fullTunnel.apps"] = "Mọi game và ứng dụng (IPv4)"' in vi_text, (
        "StringsVi fullTunnel.apps must clearly label IPv4 mode"
    )
    assert '["fullTunnel.policy.tip"]' in vi_text, "StringsVi must define fullTunnel.policy.tip"
    assert "không có kill switch" in vi_text.lower() or "no kill switch" in vi_text.lower(), (
        "fullTunnel policy in StringsVi must state no kill switch"
    )
    assert '["fullTunnel.routes"]' in vi_text, "StringsVi must define fullTunnel.routes"
    assert '["fullTunnel.hint"] = "Full tunnel: toàn bộ Internet đi qua VPS. IPv6 bị chặn khi kết nối."' not in vi_text, (
        "StringsVi must not claim IPv6 is blocked without qualification"
    )

    print("ALL HOLLOW UI AND LOCALIZATION CONTRACT CHECKS PASSED!")

if __name__ == "__main__":
    try:
        run_checks()
    except AssertionError as e:
        print(f"CHECK FAILED: {e}", file=sys.stderr)
        sys.exit(1)
