# Lunigram

<img width="2172" height="724" alt="Lunigram — Unigram for Linux" src="https://github.com/user-attachments/assets/e00da11a-d7c9-45a0-b0f3-3f72820eb7eb" />

**Unigram for Linux.**

Lunigram is a community port of [Unigram](https://github.com/UnigramDev/Unigram), bringing the familiar Unigram experience from Windows to Linux using [Uno Platform](https://platform.uno/) and Skia.

The goal is fairly simple: keep as much of Unigram as possible while making it feel at home and work reliably on Linux.

Lunigram is still a work in progress, but the main application is already usable and many core features are working.

## What works

The Linux build currently supports:

* Chats and message history
* Chat scrolling
* Chat themes
* Wallpapers, including previewing and applying them
* Avatars
* Profile Posts, including the grid view and Ctrl+wheel zoom
* One-to-one voice calls
* Local camera during calls
* Group voice calls
* Group call participants, mute and leave controls
* Group video with local and remote cameras

There is still plenty to test, especially across different Linux distributions, desktop environments and hardware.

If something is not listed here, it does not necessarily mean that it is broken — it may simply not have been thoroughly tested yet.

## Known issues

A few parts still need work:

* Screen sharing is not supported yet.
* Wayland portal integration still needs testing.
* Forum topics have a known scrolling issue.
* Some work inherited from the recent Unigram base update is still being cleaned up.
* The tint effect can currently produce harmless log noise.
* Linux toast/notification assets still need some attention.

Bug reports and testing on different Linux setups are welcome.

## Install

Prebuilt packages are available for AppImage, Flatpak and Debian/Ubuntu.

Current release builds are unsigned, so SHA256 hashes are provided for verification.

### AppImage

`Unigram-12.10.2-x86_64-UNSIGNED.AppImage`

SHA256:

`749be9cc3c7acb34efaabd24abcbb588ca9b0c17a348d0fa6240e55ceb22666f`

```sh
chmod +x Unigram-12.10.2-x86_64-UNSIGNED.AppImage
./Unigram-12.10.2-x86_64-UNSIGNED.AppImage
```

### Flatpak

`Unigram-12.10.2-x86_64-UNSIGNED.flatpak`

SHA256:

`f42a892e41fc5d05671ef19837196451caffb86021afd951dc92ab337f1e01a4`

```sh
flatpak install --user ./Unigram-12.10.2-x86_64-UNSIGNED.flatpak
```

### Debian / Ubuntu

`unigram-linux_12.10.2_amd64-UNSIGNED.deb`

SHA256:

`d927db4a1e1eb864bff1d78b4008ee54e880532480574991e09a64aec1592fca`

```sh
sudo apt install ./unigram-linux_12.10.2_amd64-UNSIGNED.deb
```

The Debian package installs Lunigram under:

```text
/opt/unigram-linux
```

with the launcher available at:

```text
/usr/bin/unigram
```

A bundle of the native libraries used by the project is also available for developers:

`unigram-linux-prebuilt-native-libs-x86_64.tar.zst`

SHA256:

`e406016c363ea9c6219dc1446ff9324bb10361f0c044f6a772689d79b82b9b00`

## Building Lunigram

If you want to build the project yourself, see [BUILDING-LINUX.md](BUILDING-LINUX.md).

The build currently requires the native libraries from the companion `unigram-linux` repository as well as a matching TDLib scheme.

You will also need your own Telegram `api_id` and `api_hash`, which can be created at [my.telegram.org](https://my.telegram.org/apps).

Copy:

```text
Telegram/Constants.Secret.cs.template
```

to:

```text
Telegram/Constants.Secret.cs
```

and add your credentials there.

`Constants.Secret.cs` is ignored by Git and should never be committed.

## About the project

Lunigram is based on the work of the [Unigram](https://github.com/UnigramDev/Unigram) project and its contributors.

It is an independent community port and is not affiliated with or endorsed by Telegram, Telegram Messenger LLP, Unigram or UnigramDev.

Telegram is a trademark of its respective owner.

The project is released under the GNU General Public License v3. See [LICENSE](LICENSE) for details and [NOTICE](NOTICE) for information about the third-party native components used by the Linux build.
