# Unigram for Linux

This is an **unofficial community Linux port of [Unigram](https://github.com/UnigramDev/Unigram)**, the Telegram client for Windows, adapted to Linux with [Uno Platform](https://platform.uno/) and Skia.

It is not affiliated with, endorsed by, or an official distribution of Telegram, Telegram Messenger LLP, Unigram, or UnigramDev. Unigram and the original work are credited to [UnigramDev/Unigram](https://github.com/UnigramDev/Unigram). Telegram is a trademark of its respective owner.

The port is based on Unigram 12.10.2. It is released under the GNU General Public License v3; see [LICENSE](LICENSE).

## What works

The following have been exercised on screen in the Linux build:

- Chats and scrolling
- Chat themes
- Wallpapers: rendering, preview, and applying a wallpaper
- A profile's Posts tab: loading and grid display, including Ctrl+wheel zoom
- Avatars
- One-to-one voice calls with the local camera
- Group voice calls: call window, participants, mute, and leave
- Group video: local and remote camera video

These are reports of the tested port, not a claim that every Telegram feature or every hardware and desktop combination behaves identically.

## Known limitations and unverified areas

The following are not yet supported, remain under verification, or have known issues:

- Screen sharing is not yet supported.
- Wayland portal integration has not been verified.
- Forum-topic navigation still has a scroll issue.
- Some base-bump residue items remain: page-block follow-up, pointer-position, and selectable-offsets work.
- A tint-effect guard still produces log noise.
- The Linux toast asset glob still needs attention.

If a capability is not listed as working above, treat it as **unverified** rather than assuming parity with the Windows client.

## Install

Release artifacts are currently **unsigned**. The SHA256 values below provide an integrity check; a human may sign and rename the files before upload, including removing the `-UNSIGNED` suffix.

Packaged builds bundle their own copy of libopus; loader resolution to the bundled copy is verified at packaging time, but a live voice call is not exercised during packaging.

### AppImage

`Unigram-12.10.2-x86_64-UNSIGNED.AppImage`  
Size: 92,494,328 bytes  
SHA256: `749be9cc3c7acb34efaabd24abcbb588ca9b0c17a348d0fa6240e55ceb22666f`

Run it after making it executable:

```sh
chmod +x Unigram-12.10.2-x86_64-UNSIGNED.AppImage
./Unigram-12.10.2-x86_64-UNSIGNED.AppImage
```

### Flatpak

`Unigram-12.10.2-x86_64-UNSIGNED.flatpak`  
Size: 76,343,184 bytes  
SHA256: `f42a892e41fc5d05671ef19837196451caffb86021afd951dc92ab337f1e01a4`

```sh
flatpak install --user ./Unigram-12.10.2-x86_64-UNSIGNED.flatpak
```

### Debian / Ubuntu (.deb)

`unigram-linux_12.10.2_amd64-UNSIGNED.deb`  
Size: 79,898,968 bytes  
SHA256: `d927db4a1e1eb864bff1d78b4008ee54e880532480574991e09a64aec1592fca`

```sh
sudo apt install ./unigram-linux_12.10.2_amd64-UNSIGNED.deb
```

The package installs its payload under `/opt/unigram-linux` with a launcher at `/usr/bin/unigram`.

The native-library bundle is also available for builders:

`unigram-linux-prebuilt-native-libs-x86_64.tar.zst`  
Size: 23,759,472 bytes  
SHA256: `e406016c363ea9c6219dc1446ff9324bb10361f0c044f6a772689d79b82b9b00`

## Build from source

See [BUILDING-LINUX.md](BUILDING-LINUX.md) for the authoritative prerequisites and build steps. A clean build needs the native libraries from the companion `unigram-linux` repository and a matching TDLib scheme.

Each builder must provide their own Telegram `api_id` and `api_hash` from [my.telegram.org](https://my.telegram.org/apps), using `Telegram/Constants.Secret.cs.template` to create the gitignored `Telegram/Constants.Secret.cs`. Do not commit credentials or share a filled-in copy.

## Credits and license

The application is derived from Unigram by UnigramDev and incorporates third-party native components. See [NOTICE](NOTICE) for the native component inventory and build locations. Source is licensed under GPLv3; see [LICENSE](LICENSE).
