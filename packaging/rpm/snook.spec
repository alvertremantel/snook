Name:           snook
%{!?snook_version:%global snook_version 0.1.0}
Version:        %{snook_version}
Release:        3%{?dist}
Summary:        Local-first task and time system
License:        Proprietary
URL:            https://github.com/snook
ExclusiveArch:  x86_64
Source0:        Snook-%{version}-linux-x64.tar.gz
Source1:        com.snook.Snook.desktop
Source2:        snookd.service
Source3:        README.md
Source4:        operations.md
Source5:        cli.md
Source6:        json-export.md
%global debug_package %{nil}
# Bundled runtime libraries are private, not providers for other RPMs.
%global __provides_exclude_from ^%{_libdir}/snook/.*$
# The runtime's debugger shim uses this library from the same private payload.
%global __requires_exclude ^libmscordaccore[.]so.*$
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
Requires:       systemd
# Self-contained .NET still loads native OS facilities at runtime (dlopen).
# https://learn.microsoft.com/en-us/dotnet/core/install/linux-fedora#dependencies
Requires:       glibc%{?_isa}
Requires:       libgcc%{?_isa}
Requires:       libstdc++%{?_isa}
Requires:       openssl-libs%{?_isa}
Requires:       libicu%{?_isa}
Requires:       krb5-libs%{?_isa}
Requires:       ca-certificates
Requires:       tzdata

%description
Snook is a local-first, data-private task and time system with an embedded
SQLite workspace, an Avalonia desktop interface, a structured-JSON CLI,
and an optional authenticated loopback daemon with a per-user systemd unit.

%prep
%setup -q -n Snook-%{version}-linux-x64

%build

%install
install -d %{buildroot}%{_libdir}/snook
cp -a . %{buildroot}%{_libdir}/snook/
install -d %{buildroot}%{_bindir}
cat > %{buildroot}%{_bindir}/snook <<'EOF'
#!/usr/bin/sh
exec %{_libdir}/snook/Desktop/Snook "$@"
EOF
chmod 0755 %{buildroot}%{_bindir}/snook
cat > %{buildroot}%{_bindir}/snook-cli <<'EOF'
#!/usr/bin/sh
exec %{_libdir}/snook/Cli/snook "$@"
EOF
cat > %{buildroot}%{_bindir}/snookd <<'EOF'
#!/usr/bin/sh
exec %{_libdir}/snook/Daemon/snookd "$@"
EOF
chmod 0755 %{buildroot}%{_bindir}/snook-cli %{buildroot}%{_bindir}/snookd
install -d %{buildroot}%{_datadir}/applications
install -m 0644 %{_sourcedir}/com.snook.Snook.desktop \
  %{buildroot}%{_datadir}/applications/com.snook.Snook.desktop
install -d %{buildroot}%{_prefix}/lib/systemd/user
install -m 0644 %{SOURCE2} %{buildroot}%{_prefix}/lib/systemd/user/snookd.service
install -d %{buildroot}%{_docdir}/snook/docs
install -m 0644 %{SOURCE3} %{buildroot}%{_docdir}/snook/README.md
install -m 0644 %{SOURCE4} %{SOURCE5} %{SOURCE6} %{buildroot}%{_docdir}/snook/docs/

%files
%{_bindir}/snook
%{_bindir}/snook-cli
%{_bindir}/snookd
%{_libdir}/snook/
%{_datadir}/applications/com.snook.Snook.desktop
%{_prefix}/lib/systemd/user/snookd.service
%doc %{_docdir}/snook/

%changelog
* Fri Sep 18 2026 Snook contributors - 0.1.0-3
- Add contract 1.6 caller-owned create retries and safe maintenance destinations
- Export schema 5 with settings, task relationships and retained deletion history
- Ship the versioned JSON export reference

* Fri Sep 18 2026 Snook contributors - 0.1.0-2
- Package CLI and daemon alongside the existing GUI launcher
- Add an opt-in per-user service and native runtime dependencies

* Wed Sep 09 2026 Snook contributors - 0.1.0-1
- Initial Fedora x86_64 desktop package
