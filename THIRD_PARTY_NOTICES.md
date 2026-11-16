# Third-party notices

WinIsland is distributed under the GNU General Public License v3.0. See
[`LICENSE`](LICENSE) for the full text.

## OPPO Pods for Windows

The earbuds control implementation under `Vendor/OppoPods.Core` and
`Vendor/OppoPods.Session` is derived from:

- [3295074384/OppoPodsWindows](https://github.com/3295074384/OppoPodsWindows) — GPL-3.0
- [Leaf-lsgtky/OppoPods](https://github.com/Leaf-lsgtky/OppoPods) — GPL-3.0
- [Art-Chen/HyperPods](https://github.com/Art-Chen/HyperPods) — GPL-3.0

The upstream code provides an unofficial, reverse-engineered implementation of
the OPPO / HeyMelody RFCOMM control protocol. It is not affiliated with or
endorsed by OPPO, OnePlus, realme, HeyTap, or Microsoft.

The vendored copy has been minimally adapted for WinIsland:

- retargeted to `net10.0-windows10.0.19041.0`
- nullable warnings fixed
- project layout kept under `Vendor/` so the WinUI application layer is not
  included

If you redistribute WinIsland or a modified version, keep this notice, the
GPL-3.0 license, and the corresponding source code available.
