Name:           snook
Version:        0.1.0
Release:        1%{?dist}
Summary:        Local-first task and time system
License:        Proprietary
URL:            https://github.com/snook
ExclusiveArch:  x86_64
Source0:        Snook-%{version}-linux-x64.tar.gz
Source1:        com.snook.Snook.desktop
%global debug_package %{nil}
Requires:       libX11
Requires:       libXcursor
Requires:       libXext
Requires:       libXfixes
Requires:       libXi
Requires:       libXrandr
Requires:       mesa-libGL
Requires:       fontconfig
Requires:       freetype
Requires:       glib2

%description
Snook is a local-first, data-private task and time system with an embedded
SQLite workspace and an Avalonia desktop interface.

%prep
%setup -q -n Snook-%{version}-linux-x64

%build

%install
install -d %{buildroot}%{_libdir}/snook
cp -a . %{buildroot}%{_libdir}/snook/
install -d %{buildroot}%{_bindir}
cat > %{buildroot}%{_bindir}/snook <<'EOF'
#!/bin/sh
exec /usr/lib64/snook/Snook "$@"
EOF
chmod 0755 %{buildroot}%{_bindir}/snook
install -d %{buildroot}%{_datadir}/applications
install -m 0644 %{_sourcedir}/com.snook.Snook.desktop \
  %{buildroot}%{_datadir}/applications/com.snook.Snook.desktop

%files
%{_bindir}/snook
%{_libdir}/snook/
%{_datadir}/applications/com.snook.Snook.desktop

%changelog
* Wed Sep 09 2026 Snook contributors - 0.1.0-1
- Initial Fedora x86_64 desktop package
