# Third-party notices

WinIsland is distributed under the GNU General Public License v3.0. See
[`LICENSE`](LICENSE) for the full text.

## EncoDeck

The multi-brand earbuds engine under `Vendor/EncoDeck.Core`,
`Vendor/EncoDeck.Protocol` and `Vendor/EncoDeck.Bluetooth` is derived from:

- [TIANLI0/EncoDeck](https://github.com/TIANLI0/EncoDeck) — GPL-3.0-only

EncoDeck itself credits the following protocol research and upstream projects:

- [3295074384/OppoPodsWindows](https://github.com/3295074384/OppoPodsWindows) — GPL-3.0
- [Leaf-lsgtky/OppoPods](https://github.com/Leaf-lsgtky/OppoPods) — GPL-3.0
- [Art-Chen/HyperPods](https://github.com/Art-Chen/HyperPods) — GPL-3.0

The upstream code provides an unofficial, reverse-engineered implementation of
vendor Bluetooth control protocols. It is not affiliated with or endorsed by
OPPO, OnePlus, realme, Huawei, Xiaomi, vivo, Sony, EDIFIER, Sennheiser, Bose,
Samsung, Google, Nothing, or any other headphone brand.

The vendored copy has been minimally adapted for WinIsland:

- Windows projects target `net10.0-windows10.0.26100.0` with a `10.0.19041.0`
  supported-OS floor
- `ImplicitUsings` and nullable annotations enabled
- project layout kept under `Vendor/` so the WinUI application layer, device
  artwork and updater are not included

If you redistribute WinIsland or a modified version, keep this notice, the
GPL-3.0 license, and the corresponding source code available.
