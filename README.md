<p align="center">
  <img src="https://img.shields.io/github/v/release/iChris4/WheelWizard_VR?color=green&style=for-the-badge" alt="GitHub release (latest by date)" />
  <img src="https://img.shields.io/github/downloads/iChris4/WheelWizard_VR/total?color=green&style=for-the-badge" alt="GitHub Downloads (all assets, all releases)" />
  <a href="https://discord.gg/vZ7T2wJnsq">
    <img src="https://img.shields.io/discord/1253384439937896560?color=7289da&style=for-the-badge" alt="Discord" />
  </a>
</p>

<p align="center"><a href="https://github.com/TeamWheelWizard/WheelWizard">Wheel wizard</a> by <span>Patchzy and WantToBeeMe</span> is licensed under <a href="https://www.gnu.org/licenses/gpl-3.0.html" target="_blank" rel="license noopener noreferrer" style="display:inline-block;">GNU General Public License v3.0</a></p>

# WheelWizard VR

Windows x64 launcher fork for normal WiiCompiled and WiiCompiled OpenXR VR. Download
[`WheelWizardVRWindows.exe`](https://github.com/iChris4/WheelWizard_VR/releases/latest) from this fork.
Its self-updater uses only `iChris4/WheelWizard_VR`.

1. Select your own clean PAL **RMCP01** Mario Kart Wii disc image in Settings.
2. Open **Settings → Other → WiiCompiled (beta)**. Enable **Enable WiiCompiled (beta)** for the
   normal port or **Enable WiiCompiled OpenXR VR (beta)** for VR. Turning both off selects Dolphin.
3. Press **Install** on Home. Both Base game and Retro Rewind are compiled locally. Then choose
   **Base game** or **Retro Rewind** on Home and press Play.

The VR setup comes from [iChris4/Wiicompiled_VR](https://github.com/iChris4/Wiicompiled_VR/releases).
The normal setup continues to come from `patchzyy/Wiicompiled`. The `Recomp` and `RecompVR`
installations, downloads, build tools, graphics settings, and caches remain separate. Preferences
live in `config-vr.json`; your official WheelWizard preferences are imported once and kept intact.
The fork registers `wheelwizardvr://` links without replacing official WheelWizard's URL handler.

Both recompilations use the normal installation's effective NAND, including a custom NAND or the
existing Dolphin share/copy choice. Saves and Miis stay shared. Retro Rewind's XML-directed saves
and ghosts retain their separate locations and survive content updates. Controller mappings are
copied into VR once when available; later mapping changes are independent. Uninstall preserves
configuration, shared NAND, imported saves, and Retro Rewind content.

Retro Rewind downloads and deletions are applied to a staging copy before publication. Cancellation
keeps the current content and version intact. A journal restores the previous content if publication
is interrupted. Updates and reinstalls preserve existing ghosts, saves, custom XML save directories,
and local patches. Allow additional free space for the staging copy during updates.

Under **Settings → OpenXR VR → Virtual reality**, choose whether to enable OpenXR, the desktop
mirror view, chase or first-person camera, how the camera follows kart rotation, driver visibility,
and headset render scale. These preferences apply on the next launch and remain separate from
the normal installation. Turning OpenXR off runs the VR installation on desktop until re-enabled.

VR launches use D3D12 and enable OpenXR initially. A missing runtime or headset falls back to the desktop
with an explanation in the game; see **F10 → VR**. Backend/game switches and shared-data edits are
blocked during managed gameplay and setup. Other copies of this launcher share the same lock.
Directly executing older game binaries is outside this guarantee.

Build and verify a Windows release with .NET 10:

```powershell
dotnet test WheelWizard.sln -c Release
dotnet publish WheelWizard/WheelWizard.csproj -r win-x64 -c Release --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -o publish
./Verify-Release.ps1 -Tag v2.5.5 -ExecutablePath publish/WheelWizard.exe
```

`Directory.Build.props` is the version source. The Windows release workflow verifies the version,
runs tests, and publishes only the launcher executable and checksum. Publish and validate the VR
backend before releasing this launcher. No disc images or locally compiled games are distributed.

The following describes the upstream project and credits its authors.

<p align="center">
  <img src="https://github.com/TeamWheelWizard/.github/blob/main/images/WheelWizard_text_icon.png" alt="Wheel Wizard Logo" width="500"/>
</p>

## Mario Kart Mod Manager & Retro Rewind Auto Updater

Wheel Wizard, our mod manager, is created for the sole purpose of convenience. Technically, these mods and Retro Rewind are all doable without this app, but this app makes it possible with just a few clicks. It still has a lot of features in the planning stage and is fully in development, so keep an eye out for updates.

## Free and Open Source

Wheel Wizard was made by [patchzy](https://github.com/patchzyy) and [wanttobeeme](https://github.com/wanttobeeme). This application is completely free to use. You can go to the [latest releases](https://github.com/iChris4/WheelWizard_VR/releases) and download the executable there. Once installed, the app will automatically notify you when updates are available.

Feel free to join our community on [Discord](https://discord.gg/vZ7T2wJnsq) for support and discussions! If you see any bugs, this is also the place to be :P


<p align="center">
  <img src="https://github.com/TeamWheelWizard/.github/blob/main/images/screenshots/home_page.png" alt="Wheel Wizard Logo" width="450"/>
  <img src="https://github.com/TeamWheelWizard/.github/blob/main/images/screenshots/rooms_page.png" alt="Wheel Wizard Logo" width="450"/>
  <img src="https://github.com/TeamWheelWizard/.github/blob/main/images/screenshots/profile_page.png" alt="Wheel Wizard Logo" width="450"/>
  <img src="https://github.com/TeamWheelWizard/.github/blob/main/images/screenshots/mods_browser.png" alt="Wheel Wizard Logo" width="450"/>
  <img src="https://github.com/TeamWheelWizard/.github/blob/main/images/screenshots/miieditor_page.png" alt="Wheel Wizard Logo" width="450"/>
  <img src="https://github.com/TeamWheelWizard/.github/blob/main/images/screenshots/mii_page.png" alt="Wheel Wizard Logo" width="450"/>
</p>
---

## Antivirus Warning

### False Positive Detections

Some antivirus software, including Windows Defender, may incorrectly flag Wheel Wizard as a virus or trojan. 
This is a common issue with new software releases and is known as a "false positive." 
Rest assured, Wheel Wizard is safe to use.

### Why This Happens

- Antivirus programs sometimes mistakenly identify new or less common software as potentially harmful.
- These false detections can vary between different antivirus programs and even between scans.
- The inconsistency in these detections (e.g., being labeled as both a "virus" and a "trojan") further indicates that these are likely false positives.

### What You Can Do

1. **Ensure you're using the latest version** of Wheel Wizard, as we continually work to address these issues.
2. If you encounter a warning, you can submit the Wheel Wizard executable to Microsoft for analysis [here](https://www.microsoft.com/en-us/wdsi/filesubmission). This helps improve detection accuracy.
3. You may need to add an exception for Wheel Wizard in your antivirus software to prevent it from interfering with the application.
4. If you dont trust us, you may either build the program yourself (we have included a build.bat in our source code)
5. Or you can run the program through [virustotal](https://www.virustotal.com/gui/home/upload) and see it will pass 99% of checks
   
### What We're Doing About It

- We're actively working on submitting each release to Microsoft and other antivirus vendors for review.
- This process should help reduce false positive detections in future scans.
- We're exploring options for code signing, which may help prevent these issues in the long term.

### Trust and Verification

We understand that security is paramount when downloading software. We encourage users to:

- Verify that you're downloading Wheel Wizard from our official GitHub repository.
- Check the code yourself if you have concerns – we're open source for a reason!
- Join our [Discord community](https://discord.gg/vZ7T2wJnsq) if you have any questions or concerns.

Remember, while we assure you of our software's safety, it's always good practice to exercise caution when downloading and running new applications.

## License

Wheel Wizard is licensed under the [GNU General Public License v3.0](https://www.gnu.org/licenses/gpl-3.0.html).  
You are free to use, modify, and distribute this software, provided any derivative works are also licensed under the GPL v3.0.

## Sources

Retro Rewind was made by ZPL. More information about Retro Rewind can be found on the [Tockdom Wiki](https://wiki.tockdom.com/wiki/Retro_Rewind).
Huge parts from the mii renderer were inspired and ported from [ariankordi's branch](https://github.com/ariankordi/FFL-Testing) of abood's [FFL-Testing](https://github.com/aboood40091/FFL-Testing) project. You can find their website [here](https://mii-unsecure.ariankordi.net/).
Some of the icons used in Wheel Wizard are from [Game Icons](https://game-icons.net/about.html). Specifically, the [car wheel icon](https://game-icons.net/1x1/delapouite/car-wheel.html) and the [flat tire icon](https://game-icons.net/1x1/delapouite/flat-tire.html), both created by Delapouite.
Also thanks Chadderz' for the specials icons in ["Chadderz' Terrible Mario Kart Font"](https://wiki.tockdom.com/wiki/CTMKF)
