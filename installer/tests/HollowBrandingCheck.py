import re
import sys
from pathlib import Path

def run_checks():
    repo_root = Path(__file__).resolve().parent.parent.parent
    print(f"Checking repo root: {repo_root}")

    # 1. VERSION file check
    version_file = repo_root / "VERSION"
    assert version_file.exists(), "VERSION file missing"
    version_text = version_file.read_text(encoding="utf-8").strip()
    assert re.fullmatch(r"\d+\.\d+\.\d+", version_text), f"VERSION expected numeric x.y.z, got '{version_text}'"

    # Validate SETUP-NOTES.txt header against actual version
    setup_notes = repo_root / "installer" / "SETUP-NOTES.txt"
    if setup_notes.exists():
        assert f"v{version_text}" in setup_notes.read_text(encoding="utf-8"), f"SETUP-NOTES.txt must mention v{version_text}"

    # 2. LICENSE file check
    license_file = repo_root / "LICENSE"
    assert license_file.exists(), "LICENSE file missing"
    license_text = license_file.read_text(encoding="utf-8")
    assert "Viet Nguyen" in license_text, "LICENSE must preserve original copyright (Viet Nguyen)"
    assert "2026 Hollow" in license_text, "LICENSE must add copyright 2026 Hollow for modifications"
    assert "0.5.7" not in license_text, "LICENSE must not falsely claim vendor 0.5.7 source"

    # 3. Build metadata: client/Directory.Build.props
    props_file = repo_root / "client" / "Directory.Build.props"
    assert props_file.exists(), "client/Directory.Build.props missing"
    props_text = props_file.read_text(encoding="utf-8")
    assert "<Product>Hollow Ping Booster</Product>" in props_text, "Product must be 'Hollow Ping Booster'"
    assert "<Company>Hollow Ping Booster</Company>" in props_text, "Company must be 'Hollow Ping Booster'"
    assert "<Copyright>Copyright (c) 2026 Hollow</Copyright>" in props_text, "Binary branding must identify Hollow"
    assert "Viet Nguyen" not in props_text, "Upstream author must not appear in Hollow binary branding"

    # 4. StringsEn.cs - pipe.unreachable must point to HollowPingBooster
    strings_en_file = repo_root / "client" / "src" / "GamePingBooster.App" / "Services" / "Localization" / "StringsEn.cs"
    assert strings_en_file.exists(), "StringsEn.cs missing"
    strings_en = strings_en_file.read_text(encoding="utf-8")
    assert '["pipe.unreachable"] = "Could not reach the background service. Check that the \'HollowPingBooster\' service is running."' in strings_en, (
        "StringsEn.cs pipe.unreachable must refer to 'HollowPingBooster'"
    )
    assert "Check that the 'GamePingBooster' service is running." not in strings_en, (
        "StringsEn.cs pipe.unreachable must not refer to 'GamePingBooster'"
    )

    # 5. StringsVi.cs - pipe.unreachable must point to HollowPingBooster
    strings_vi_file = repo_root / "client" / "src" / "GamePingBooster.App" / "Services" / "Localization" / "StringsVi.cs"
    assert strings_vi_file.exists(), "StringsVi.cs missing"
    strings_vi = strings_vi_file.read_text(encoding="utf-8")
    assert '["pipe.unreachable"] = "Không liên lạc được với dịch vụ nền. Kiểm tra xem dịch vụ \'HollowPingBooster\' có đang chạy không."' in strings_vi, (
        "StringsVi.cs pipe.unreachable must refer to 'HollowPingBooster'"
    )
    assert "dịch vụ 'GamePingBooster' có đang chạy không" not in strings_vi, (
        "StringsVi.cs pipe.unreachable must not refer to 'GamePingBooster'"
    )

    # 6. AboutViewModel.cs - LicenceTitle and Author/attribution
    about_vm_file = repo_root / "client" / "src" / "GamePingBooster.App" / "ViewModels" / "AboutViewModel.cs"
    assert about_vm_file.exists(), "AboutViewModel.cs missing"
    about_vm = about_vm_file.read_text(encoding="utf-8")
    assert 'public string Author => "Hollow";' in about_vm, "About must identify Hollow as the product author"
    assert 'vietnguyen010@gmail.com' not in about_vm, "Fork support must not be sent to the upstream author's email"
    assert 'public string LicenceTitle => "MIT License - Copyright (c) 2026 Hollow";' in about_vm, (
        "AboutViewModel.cs LicenceTitle must identify Hollow"
    )
    assert "Viet Nguyen" not in about_vm, "Upstream author must not appear in Hollow About branding"

    # Product-facing documents use Hollow branding. The upstream MIT notice remains in LICENSE.
    for document in [repo_root / "README.md", repo_root / "docs" / "HOLLOW-SELFHOST.md", repo_root / "installer" / "SETUP-NOTES.txt"]:
        assert "Viet Nguyen" not in document.read_text(encoding="utf-8"), f"{document.name} must not contain upstream product branding"

    # 7. UpdateChecker.cs - UserAgent and asset safety
    update_checker_file = repo_root / "client" / "src" / "GamePingBooster.App" / "Services" / "UpdateChecker.cs"
    assert update_checker_file.exists(), "UpdateChecker.cs missing"
    update_checker = update_checker_file.read_text(encoding="utf-8")
    assert 'ProductInfoHeaderValue("HollowPingBooster"' in update_checker, (
        "UpdateChecker UserAgent must be HollowPingBooster"
    )
    assert 'var name = $"HollowPingBooster-Setup-{version}.exe";' in update_checker, (
        "UpdateChecker must only search for HollowPingBooster-Setup-{version}.exe"
    )

    # 8. UpdateInstaller.cs - UserAgent
    update_installer_file = repo_root / "client" / "src" / "GamePingBooster.App" / "Services" / "UpdateInstaller.cs"
    assert update_installer_file.exists(), "UpdateInstaller.cs missing"
    update_installer = update_installer_file.read_text(encoding="utf-8")
    assert 'ProductInfoHeaderValue("HollowPingBooster"' in update_installer, (
        "UpdateInstaller UserAgent must be HollowPingBooster"
    )

    # 9. README.md check
    readme_file = repo_root / "README.md"
    assert readme_file.exists(), "README.md missing"
    readme_text = readme_file.read_text(encoding="utf-8")
    assert f"v{version_text}" in readme_text or "v0.0.1" in readme_text, f"README.md must mention v{version_text}"
    assert "ping.hollow-agent.xyz:51820" in readme_text, "README.md must document ping.hollow-agent.xyz:51820"
    assert "2026 Hollow" in readme_text, "README.md must document Hollow copyright"
    assert "does not claim source parity with the closed 0.5.7 release" in readme_text, (
        "README.md must explicitly disclaim 0.5.7 parity"
    )

    # 10. HOLLOW-SELFHOST.md check
    selfhost_file = repo_root / "docs" / "HOLLOW-SELFHOST.md"
    assert selfhost_file.exists(), "docs/HOLLOW-SELFHOST.md missing"
    selfhost_text = selfhost_file.read_text(encoding="utf-8")
    assert f"v{version_text}" in selfhost_text or "v0.0.1" in selfhost_text, f"docs/HOLLOW-SELFHOST.md must mention v{version_text}"
    assert "ping.hollow-agent.xyz:51820" in selfhost_text, "docs/HOLLOW-SELFHOST.md must document ping.hollow-agent.xyz:51820"
    assert "2026 Hollow" in selfhost_text, "docs/HOLLOW-SELFHOST.md must document Hollow copyright"

    assert 'public bool CanInstall => false;' in update_checker, (
        "Fresh-install-only releases must not offer an automatic in-place upgrade"
    )

    print("ALL HOLLOW BRANDING AND IDENTITY CHECKS PASSED!")

if __name__ == "__main__":
    try:
        run_checks()
    except AssertionError as e:
        print(f"REGRESSION FAILURE: {e}", file=sys.stderr)
        sys.exit(1)
