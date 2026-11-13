# Included software

StreamDock is GPL-3.0-or-later. The Windows executable contains the .NET Desktop
runtime and an archive of the following unmodified upstream tools. They are
extracted into the current user's LocalAppData directory on first use. Nothing
is installed system-wide. Precise versions, download URLs and SHA-256 hashes
are recorded in `scripts/dependencies.lock.json`, also embedded in the EXE.

| Component | License | Upstream/source |
| --- | --- | --- |
| .NET / WPF / Windows Forms | MIT, runtime notices | https://github.com/dotnet/runtime ; https://github.com/dotnet/wpf ; https://github.com/dotnet/winforms |
| yt-dlp (official Windows bundle) | Unlicense; bundled components have their own licenses | https://github.com/yt-dlp/yt-dlp |
| yt-dlp EJS (inside yt-dlp bundle) | Unlicense | https://github.com/yt-dlp/ejs |
| FFmpeg, static GPL build | GPL-3.0-or-later and bundled-library licenses | https://github.com/yt-dlp/FFmpeg-Builds ; https://github.com/FFmpeg/FFmpeg |
| Deno | MIT, third-party notices | https://github.com/denoland/deno |
| CPython embedded distribution | PSF-2.0, bundled notices in LICENSE.txt | https://www.python.org ; https://github.com/python/cpython |
| gallery-dl | GPL-2.0-only | https://github.com/mikf/gallery-dl |
| requests | Apache-2.0 | https://github.com/psf/requests |
| urllib3 | MIT | https://github.com/urllib3/urllib3 |
| certifi | MPL-2.0 (CA bundle) | https://github.com/certifi/python-certifi |
| charset-normalizer | MIT | https://github.com/jawah/charset_normalizer |
| idna | BSD-3-Clause | https://github.com/kjd/idna |

Python wheel license files and FFmpeg archive notices are retained in the
embedded payload. Use Settings → component folder to inspect them.

For redistribution, preserve these notices and provide corresponding source
for all copyleft components, including FFmpeg's configured libraries and build
scripts, under their upstream licenses. This repository's private release is
intended for its owner's use. No signing certificate, Spotify Client Secret,
user cookies or account tokens are included. Spotify is used only for metadata;
external media comes from the source chosen by the user.
