#!/usr/bin/env bash
set -Eeuo pipefail

IFS=$'\n\t'

if [[ -n "${BASH_SOURCE[0]:-}" && -f "${BASH_SOURCE[0]}" ]]; then
  ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
else
  ROOT_DIR="$(pwd)"
fi
cd "$ROOT_DIR"

log() {
  echo "[INFO] $*"
}

warn() {
  echo "[WARN] $*" >&2
}

fail() {
  echo "[ERROR] $*" >&2
  exit 1
}

is_truthy() {
  case "${1,,}" in
    1|true|yes|y|on) return 0 ;;
    *) return 1 ;;
  esac
}

run_as_root() {
  if [[ "${EUID}" -eq 0 ]]; then
    "$@"
  elif command -v sudo >/dev/null 2>&1; then
    sudo "$@"
  else
    fail "This script requires root privileges (run as root or install sudo)"
  fi
}

require_file() {
  local file_path="$1"
  [[ -f "$file_path" ]] || fail "Required file not found: $file_path"
}

has_required_repo_layout() {
  [[ -f "CommonAssemblyInfo.cs" ]] &&
  [[ -f "NINA/NINA.csproj" ]] &&
  [[ -f "System.Windows.Compat/System.Windows.Compat.csproj" ]] &&
  [[ -f "packaging/debian/postinst" ]] &&
  [[ -f "packaging/debian/prerm" ]] &&
  [[ -f "packaging/systemd/pins.service" ]]
}

check_required_repo_layout() {
  require_file "CommonAssemblyInfo.cs"
  require_file "NINA/NINA.csproj"
  require_file "System.Windows.Compat/System.Windows.Compat.csproj"
  require_file "packaging/debian/postinst"
  require_file "packaging/debian/prerm"
  require_file "packaging/systemd/pins.service"
}

BUILD_CONFIGURATION="${BUILD_CONFIGURATION:-Release}"
TARGET_RUNTIME="${TARGET_RUNTIME:-linux-x64}"
PLUGIN_TARGET_FRAMEWORK="${PLUGIN_TARGET_FRAMEWORK:-net10.0}"

TARGET_USER="${TARGET_USER:-${SUDO_USER:-$USER}}"
TARGET_HOME="$(getent passwd "$TARGET_USER" | cut -d: -f6 || true)"
if [[ -z "$TARGET_HOME" ]]; then
  TARGET_HOME="/home/$TARGET_USER"
fi

PINS_REPO_URL="${PINS_REPO_URL:-https://github.com/acocalypso/pins.git}"
PINS_REPO_BRANCH="${PINS_REPO_BRANCH:-develop}"
PINS_WORKDIR="${PINS_WORKDIR:-$TARGET_HOME/pins-build-src}"
AUTO_CLONE_PINS_REPO="${AUTO_CLONE_PINS_REPO:-true}"
AUTO_INSTALL_BOOTSTRAP_TOOLS="${AUTO_INSTALL_BOOTSTRAP_TOOLS:-true}"
REUSE_SUCCESSFUL_BUILD="${REUSE_SUCCESSFUL_BUILD:-true}"

INSTALL_DIRECTORY="${INSTALL_DIRECTORY:-$TARGET_HOME/pins}"
PUBLISH_DIRECTORY="${PUBLISH_DIRECTORY:-artifacts/publish}"
PACKAGE_ROOT="${PACKAGE_ROOT:-artifacts/package}"
DEB_ROOT="${DEB_ROOT:-artifacts/debroot}"
DEB_PACKAGE="${DEB_PACKAGE:-pins}"
DEB_ARCH="${DEB_ARCH:-amd64}"

PLUGINS_INSTALL_BASE="${PLUGINS_INSTALL_BASE:-$TARGET_HOME/.local/share/NINA/Plugins/3.0.0}"
PLUGIN_DEB_BASE="${PLUGIN_DEB_BASE:-artifacts/plugin-debroot}"
PLUGIN_ARTIFACT_DIR="${PLUGIN_ARTIFACT_DIR:-artifacts/plugins}"
PLUGIN_FAILURES_FILE="${PLUGIN_FAILURES_FILE:-artifacts/plugin-build-failures.log}"

INDI_VERSION="${INDI_VERSION:-2.1.9}"
INDI_DEB_ROOT="${INDI_DEB_ROOT:-artifacts/indi-debroot}"
INDI_ENABLE_XISF="${INDI_ENABLE_XISF:-true}"
LIBXISF_AUTO_UPGRADE_IF_OLD="${LIBXISF_AUTO_UPGRADE_IF_OLD:-true}"
LIBXISF_REPO_URL="${LIBXISF_REPO_URL:-https://github.com/joxda/libXISF.git}"
LIBXISF_BRANCH="${LIBXISF_BRANCH:-master}"
LIBXISF_WORKDIR="${LIBXISF_WORKDIR:-artifacts/src/libxisf}"
LIBXISF_INSTALL_PREFIX="${LIBXISF_INSTALL_PREFIX:-/usr/local}"

PHD2_REPO_URL="${PHD2_REPO_URL:-https://github.com/acocalypso/phd2.git}"
PHD2_BRANCH="${PHD2_BRANCH:-master}"
PHD2_INDI_VERSION="${PHD2_INDI_VERSION:-2.1.9}"
PHD2_OPENCV_VERSION="${PHD2_OPENCV_VERSION:-4.11.0}"
PHD2_AUTOSTART_SERVICE="${PHD2_AUTOSTART_SERVICE:-false}"
PHD2_BUILD_INDI_3RDPARTY="${PHD2_BUILD_INDI_3RDPARTY:-true}"
PHD2_INDI_ROOT="${PHD2_INDI_ROOT:-/usr}"
OPENCV_REQUIRED_VERSION="${OPENCV_REQUIRED_VERSION:-$PHD2_OPENCV_VERSION}"
OPENCV_SOURCE_ROOT="${OPENCV_SOURCE_ROOT:-artifacts/src/opencv}"
OPENCV_INSTALL_PREFIX="${OPENCV_INSTALL_PREFIX:-/usr/local}"
OPENCV_EXTRA_MODULES="${OPENCV_EXTRA_MODULES:-true}"
OPENCV_REQUIRE_LOCAL_PKGCONFIG="${OPENCV_REQUIRE_LOCAL_PKGCONFIG:-true}"

OPENCVSHARP_REPO_URL="${OPENCVSHARP_REPO_URL:-https://github.com/shimat/opencvsharp.git}"
OPENCVSHARP_BRANCH="${OPENCVSHARP_BRANCH:-main}"
OPENCVSHARP_WORKDIR="${OPENCVSHARP_WORKDIR:-artifacts/src/opencvsharp}"
OPENCVSHARP_OPENCV_VERSION="${OPENCVSHARP_OPENCV_VERSION:-4.11.0}"
OPENCVSHARP_RUNTIME_PACKAGE="${OPENCVSHARP_RUNTIME_PACKAGE:-OpenCvSharp4.official.runtime.linux-x64}"

SETUP_RUNTIME_PREREQS="${SETUP_RUNTIME_PREREQS:-true}"
SETUP_FRAMINGASSISTANT_CACHE="${SETUP_FRAMINGASSISTANT_CACHE:-true}"
SETUP_ASTAP="${SETUP_ASTAP:-true}"
RUNTIME_SETUP_STRICT="${RUNTIME_SETUP_STRICT:-false}"
CREATE_FIRMWARE_BUNDLE="${CREATE_FIRMWARE_BUNDLE:-false}"

FRAMINGASSISTANT_CACHE_URL="${FRAMINGASSISTANT_CACHE_URL:-https://nighttime-imaging.eu/downloads/Setup/Releases/FramingAssistantCache_Full.zip}"
FRAMINGASSISTANT_CACHE_ROOT="${FRAMINGASSISTANT_CACHE_ROOT:-$TARGET_HOME/.local/share/NINA}"
FRAMINGASSISTANT_CACHE_DIR="${FRAMINGASSISTANT_CACHE_DIR:-$FRAMINGASSISTANT_CACHE_ROOT/FramingAssistantCache}"

ASTAP_CLI_SOURCE="${ASTAP_CLI_SOURCE:-}"
ASTAP_PRIMARY_PATH="${ASTAP_PRIMARY_PATH:-/usr/local/bin/astap_cli}"
ASTAP_ALT_PATH="${ASTAP_ALT_PATH:-/usr/bin/astap}"

BUILD_NUMBER="${BUILD_NUMBER:-${GITHUB_RUN_NUMBER:-$(date +%s)}}"
RELEASE_DATE="$(date +%d%m%Y)"
RELEASE_VERSION="v${RELEASE_DATE}-${BUILD_NUMBER}"

PLUGIN_MSBUILD_ARGS=(
  "-p:DisableImplicitSystemDrawingReference=true"
  "-p:Platform=AnyCPU"
  "-p:PlatformTarget=AnyCPU"
  "-p:SatelliteResourceLanguages=en-US"
)

BASE_VERSION=""
PACKAGE_VERSION=""
CORE_EXCLUDES_FILE=""

BUILT_DEBS=()

STATE_DIR="artifacts/cache"
BUILD_STATE_FILE="$STATE_DIR/build-state.env"
BUILD_ARTIFACT_MANIFEST="$STATE_DIR/last-successful-debs.txt"
CURRENT_SOURCE_FINGERPRINT=""

record_built_deb() {
  local deb_path="$1"
  local normalized_path=""

  if [[ "$deb_path" = /* ]]; then
    normalized_path="$deb_path"
  elif command -v realpath >/dev/null 2>&1; then
    normalized_path="$(realpath -m "$deb_path")"
  else
    normalized_path="$(cd "$(dirname "$deb_path")" && pwd)/$(basename "$deb_path")"
  fi

  BUILT_DEBS+=("$normalized_path")
}

get_remote_sha() {
  local repo_url="$1"
  local ref="${2:-}"
  local sha=""

  if [[ -n "$ref" ]]; then
    sha="$(git ls-remote --heads "$repo_url" "$ref" 2>/dev/null | awk 'NR==1 { print $1 }')"
    if [[ -z "$sha" ]]; then
      sha="$(git ls-remote "$repo_url" "refs/heads/$ref" 2>/dev/null | awk 'NR==1 { print $1 }')"
    fi
  else
    sha="$(git ls-remote "$repo_url" HEAD 2>/dev/null | awk 'NR==1 { print $1 }')"
  fi

  echo "$sha"
}

compute_source_fingerprint() {
  local pins_head
  pins_head="$(git rev-parse HEAD 2>/dev/null || true)"
  [[ -n "$pins_head" ]] || fail "Could not resolve local pins git HEAD"

  local unresolved=0
  local inputs=()

  inputs+=("pins-local:$pins_head")
  inputs+=("pins-branch:$PINS_REPO_BRANCH")
  inputs+=("runtime:$TARGET_RUNTIME")
  inputs+=("arch:$DEB_ARCH")
  inputs+=("opencvsharp-opencv:$OPENCVSHARP_OPENCV_VERSION")
  inputs+=("phd2-indi:$PHD2_INDI_VERSION")
  inputs+=("indi:$INDI_VERSION")

  local script_path="$ROOT_DIR/build-and-install-pins-x64.sh"
  if [[ -f "$script_path" ]]; then
    local script_hash
    script_hash="$(sha256sum "$script_path" | awk '{print $1}')"
    inputs+=("script:$script_hash")
  fi

  local tracked_sources=(
    "phd2|$PHD2_REPO_URL|$PHD2_BRANCH"
    "pins-external|https://github.com/nitr57/pins.external|main"
    "touch-plugin|https://github.com/nitr57/N.I.N.A-Plugin-for-Touch-N-Stars|develop"
    "joko|https://github.com/nitr57/joko.nina.plugins|"
    "ninaapi|https://github.com/nitr57/ninaAPI|"
    "phd2tools|https://github.com/nitr57/nina.plugin.phd2tools|"
    "orbuculum|https://github.com/nitr57/nina.plugin.orbuculum|"
    "polaralignment|https://github.com/nitr57/nina.plugin.polaralignment|"
    "livestack|https://github.com/nitr57/nina.plugin.livestack|"
    "tenmicron|https://github.com/nitr57/NINA.Joko.Plugin.TenMicron|"
    "pins-plugin|https://github.com/nitr57/pins.plugin|"
    "pinsdaemon|https://github.com/Touch-N-Stars/pinsdaemon|"
    "wandereretasdk|https://github.com/nitr57/WandererETASDK|"
    "touch-frontend|https://github.com/Touch-N-Stars/Touch-N-Stars|develop"
  )

  local item
  for item in "${tracked_sources[@]}"; do
    IFS='|' read -r name url ref <<< "$item"
    local sha
    sha="$(get_remote_sha "$url" "$ref")"
    if [[ -z "$sha" ]]; then
      unresolved=1
      sha="UNRESOLVED"
      warn "Could not resolve remote SHA for $name ($url ${ref:+@$ref})"
    fi
    inputs+=("$name:$sha")
  done

  if [[ "$unresolved" -ne 0 ]]; then
    # Force rebuild when source freshness cannot be verified reliably.
    inputs+=("unresolved-remotes:force-rebuild-$(date +%s)")
  fi

  CURRENT_SOURCE_FINGERPRINT="$(printf '%s\n' "${inputs[@]}" | sha256sum | awk '{print $1}')"
  [[ -n "$CURRENT_SOURCE_FINGERPRINT" ]] || fail "Failed to compute source fingerprint"

  log "Source fingerprint: $CURRENT_SOURCE_FINGERPRINT"
}

load_previous_build_state() {
  PREV_SOURCE_FINGERPRINT=""

  if [[ ! -f "$BUILD_STATE_FILE" ]]; then
    return
  fi

  set +u
  # shellcheck disable=SC1090
  source "$BUILD_STATE_FILE"
  set -u

  PREV_SOURCE_FINGERPRINT="${SOURCE_FINGERPRINT:-}"
}

try_reuse_successful_build() {
  if ! is_truthy "$REUSE_SUCCESSFUL_BUILD"; then
    return 1
  fi

  if [[ -z "${PREV_SOURCE_FINGERPRINT:-}" ]]; then
    return 1
  fi

  if [[ "$PREV_SOURCE_FINGERPRINT" != "$CURRENT_SOURCE_FINGERPRINT" ]]; then
    return 1
  fi

  if [[ ! -f "$BUILD_ARTIFACT_MANIFEST" ]]; then
    return 1
  fi

  mapfile -t previous_debs < "$BUILD_ARTIFACT_MANIFEST"
  if [[ ${#previous_debs[@]} -eq 0 ]]; then
    return 1
  fi

  local deb
  for deb in "${previous_debs[@]}"; do
    [[ -n "$deb" ]] || continue
    if [[ ! -f "$deb" ]]; then
      return 1
    fi
  done

  BUILT_DEBS=()
  for deb in "${previous_debs[@]}"; do
    [[ -n "$deb" ]] && BUILT_DEBS+=("$deb")
  done

  log "No source updates detected since last successful build; reusing existing artifacts"
  return 0
}

save_successful_build_state() {
  mkdir -p "$STATE_DIR"

  mapfile -t unique_debs < <(printf '%s\n' "${BUILT_DEBS[@]}" | awk 'NF && !seen[$0]++')
  if [[ ${#unique_debs[@]} -eq 0 ]]; then
    return
  fi

  printf '%s\n' "${unique_debs[@]}" > "$BUILD_ARTIFACT_MANIFEST"

  {
    echo "SOURCE_FINGERPRINT=$CURRENT_SOURCE_FINGERPRINT"
    echo "SAVED_AT=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  } > "$BUILD_STATE_FILE"
}

install_bootstrap_tools_if_needed() {
  local missing=()

  command -v git >/dev/null 2>&1 || missing+=("git")
  command -v curl >/dev/null 2>&1 || missing+=("curl")
  # without git-lfs the clone gets pointer files (e.g. NOVAS31/NOVAS31/CIO_RA.TXT) instead of their content
  git lfs version >/dev/null 2>&1 || missing+=("git-lfs")

  if [[ ${#missing[@]} -eq 0 ]]; then
    return
  fi

  if ! is_truthy "$AUTO_INSTALL_BOOTSTRAP_TOOLS"; then
    fail "Missing bootstrap tools: ${missing[*]}. Set AUTO_INSTALL_BOOTSTRAP_TOOLS=true or install them manually."
  fi

  command -v apt-get >/dev/null 2>&1 || fail "apt-get is required to install missing bootstrap tools"

  log "Installing missing bootstrap tools: ${missing[*]}"
  run_as_root apt-get update
  run_as_root apt-get install -y --no-install-recommends ca-certificates curl git git-lfs
  git lfs install
}

ensure_repo_root() {
  if has_required_repo_layout; then
    return
  fi

  if ! is_truthy "$AUTO_CLONE_PINS_REPO"; then
    fail "Not running in a pins repository checkout, and AUTO_CLONE_PINS_REPO is disabled"
  fi

  install_bootstrap_tools_if_needed

  log "Repository layout not found in current directory, bootstrapping source checkout"

  if [[ -d "$PINS_WORKDIR/.git" ]]; then
    log "Using existing repository at $PINS_WORKDIR"
    cd "$PINS_WORKDIR"
    git fetch origin "$PINS_REPO_BRANCH"
    git checkout "$PINS_REPO_BRANCH"
    git pull --ff-only origin "$PINS_REPO_BRANCH"
  else
    if [[ -e "$PINS_WORKDIR" ]]; then
      fail "PINS_WORKDIR exists but is not a git checkout: $PINS_WORKDIR"
    fi

    mkdir -p "$(dirname "$PINS_WORKDIR")"
    git clone --branch "$PINS_REPO_BRANCH" --single-branch "$PINS_REPO_URL" "$PINS_WORKDIR"
    cd "$PINS_WORKDIR"
  fi

  ROOT_DIR="$PWD"
  check_required_repo_layout
}

install_build_prerequisites() {
  log "Installing system build prerequisites"

  run_as_root apt-get update
  run_as_root apt-get install -y --no-install-recommends \
    apt-transport-https \
    autoconf \
    autoconf-archive \
    automake \
    build-essential \
    ca-certificates \
    cdbs \
    cmake \
    curl \
    debhelper \
    devscripts \
    default-jre-headless \
    dkms \
    dpkg-dev \
    equivs \
    fakeroot \
    fxload \
    gettext \
    git \
    git-lfs \
    gnupg \
    libavdevice-dev \
    libboost-regex-dev \
    libboost-all-dev \
    libcfitsio-dev \
    libczmq-dev \
    libcurl4-gnutls-dev \
    libdc1394-dev \
    libeigen3-dev \
    libev-dev \
    libfftw3-dev \
    libftdi1-dev \
    libftdi-dev \
    libgphoto2-dev \
    libgps-dev \
    libgsl-dev \
    libgtest-dev \
    libgmock-dev \
    libhidapi-dev \
    libicu-dev \
    libjpeg-dev \
    libjsoncpp-dev \
    libkrb5-dev \
    liblimesuite-dev \
    libnova-dev \
    libopencv-dev \
    libpng-dev \
    libraw-dev \
    librtlsdr-dev \
    libssl-dev \
    libtesseract-dev \
    libtheora-dev \
    libtiff-dev \
    libtool \
    libudev-dev \
    libusb-1.0-0-dev \
    libusb-dev \
    libv4l-dev \
    libwxgtk3.2-dev \
    libx11-dev \
    libxisf-dev \
    libzstd-dev \
    libzmq3-dev \
    libwebp-dev \
    ninja-build \
    nlohmann-json3-dev \
    pkg-config \
    rsync \
    unzip \
    wget \
    wx-common \
    wx3.2-i18n \
    zip \
    zlib1g-dev
}

get_opencv_version() {
  local version=""

  if [[ -x "$OPENCV_INSTALL_PREFIX/bin/opencv_version" ]]; then
    version="$($OPENCV_INSTALL_PREFIX/bin/opencv_version 2>/dev/null || true)"
  fi

  if [[ -z "$version" ]] && command -v pkg-config >/dev/null 2>&1; then
    local local_pkg_paths=()
    local multiarch
    multiarch="$(dpkg-architecture -qDEB_HOST_MULTIARCH 2>/dev/null || true)"

    local_pkg_paths+=("$OPENCV_INSTALL_PREFIX/lib/pkgconfig")
    local_pkg_paths+=("$OPENCV_INSTALL_PREFIX/lib64/pkgconfig")
    if [[ -n "$multiarch" ]]; then
      local_pkg_paths+=("$OPENCV_INSTALL_PREFIX/lib/$multiarch/pkgconfig")
    fi

    local local_pkg_path
    local_pkg_path="$(IFS=:; echo "${local_pkg_paths[*]}")"

    if PKG_CONFIG_PATH="$local_pkg_path:${PKG_CONFIG_PATH:-}" pkg-config --exists opencv4; then
      version="$(PKG_CONFIG_PATH="$local_pkg_path:${PKG_CONFIG_PATH:-}" pkg-config --modversion opencv4 2>/dev/null || true)"
    fi
  fi

  if [[ -z "$version" ]] && command -v pkg-config >/dev/null 2>&1 && pkg-config --exists opencv4; then
    version="$(pkg-config --modversion opencv4 2>/dev/null || true)"
  fi

  if [[ -z "$version" ]]; then
    if command -v opencv_version >/dev/null 2>&1; then
      version="$(opencv_version 2>/dev/null || true)"
    fi
  fi

  echo "$version"
}

get_local_opencv_pkgconfig_version() {
  if ! command -v pkg-config >/dev/null 2>&1; then
    return
  fi

  local multiarch
  multiarch="$(dpkg-architecture -qDEB_HOST_MULTIARCH 2>/dev/null || true)"

  local local_pkg_paths=()
  local_pkg_paths+=("$OPENCV_INSTALL_PREFIX/lib/pkgconfig")
  local_pkg_paths+=("$OPENCV_INSTALL_PREFIX/lib64/pkgconfig")
  if [[ -n "$multiarch" ]]; then
    local_pkg_paths+=("$OPENCV_INSTALL_PREFIX/lib/$multiarch/pkgconfig")
  fi

  local local_pkg_path
  local_pkg_path="$(IFS=:; echo "${local_pkg_paths[*]}")"

  if PKG_CONFIG_LIBDIR="$local_pkg_path" pkg-config --exists opencv4; then
    PKG_CONFIG_LIBDIR="$local_pkg_path" pkg-config --modversion opencv4 2>/dev/null || true
  fi
}

opencv_matches_required_version() {
  local installed="$1"
  [[ -n "$installed" ]] || return 1

  local required_major_minor
  required_major_minor="$(echo "$OPENCV_REQUIRED_VERSION" | awk -F. '{print $1"."$2}')"

  [[ "$installed" == "$OPENCV_REQUIRED_VERSION" || "$installed" == "$required_major_minor"* ]]
}

activate_opencv_environment() {
  local multiarch
  multiarch="$(dpkg-architecture -qDEB_HOST_MULTIARCH 2>/dev/null || true)"

  local local_pkg_paths=()
  local_pkg_paths+=("$OPENCV_INSTALL_PREFIX/lib/pkgconfig")
  local_pkg_paths+=("$OPENCV_INSTALL_PREFIX/lib64/pkgconfig")
  if [[ -n "$multiarch" ]]; then
    local_pkg_paths+=("$OPENCV_INSTALL_PREFIX/lib/$multiarch/pkgconfig")
  fi

  local existing_pkg_path="${PKG_CONFIG_PATH:-}"
  local combined_pkg_path="$existing_pkg_path"
  local pkg_path
  for pkg_path in "${local_pkg_paths[@]}"; do
    if [[ -d "$pkg_path" ]]; then
      combined_pkg_path="$pkg_path${combined_pkg_path:+:$combined_pkg_path}"
    fi
  done
  if [[ -n "$combined_pkg_path" ]]; then
    export PKG_CONFIG_PATH="$combined_pkg_path"
  fi

  local opencv_cmake_dir="$OPENCV_INSTALL_PREFIX/lib/cmake/opencv4"
  if [[ -d "$opencv_cmake_dir" ]]; then
    export OpenCV_DIR="$opencv_cmake_dir"
    export CMAKE_PREFIX_PATH="$OPENCV_INSTALL_PREFIX:${CMAKE_PREFIX_PATH:-}"
  fi

  export PATH="$OPENCV_INSTALL_PREFIX/bin:${PATH:-}"
  export LD_LIBRARY_PATH="$OPENCV_INSTALL_PREFIX/lib:${LD_LIBRARY_PATH:-}"
}

build_and_install_opencv_required_version() {
  log "Building OpenCV $OPENCV_REQUIRED_VERSION from source"

  local opencv_src_root="$OPENCV_SOURCE_ROOT"
  local opencv_zip="$opencv_src_root/opencv-${OPENCV_REQUIRED_VERSION}.zip"
  local opencv_contrib_zip="$opencv_src_root/opencv_contrib-${OPENCV_REQUIRED_VERSION}.zip"
  local opencv_src_dir="$opencv_src_root/opencv-${OPENCV_REQUIRED_VERSION}"
  local opencv_contrib_dir="$opencv_src_root/opencv_contrib-${OPENCV_REQUIRED_VERSION}"
  local opencv_build_dir="$opencv_src_root/build-${OPENCV_REQUIRED_VERSION}"

  rm -rf "$opencv_src_root"
  mkdir -p "$opencv_src_root"

  wget -O "$opencv_zip" "https://github.com/opencv/opencv/archive/refs/tags/${OPENCV_REQUIRED_VERSION}.zip"
  unzip -q "$opencv_zip" -d "$opencv_src_root"

  local cmake_args=(
    -DCMAKE_BUILD_TYPE=Release
    -DCMAKE_INSTALL_PREFIX="$OPENCV_INSTALL_PREFIX"
    -DOPENCV_GENERATE_PKGCONFIG=ON
  )

  if is_truthy "$OPENCV_EXTRA_MODULES"; then
    wget -O "$opencv_contrib_zip" "https://github.com/opencv/opencv_contrib/archive/refs/tags/${OPENCV_REQUIRED_VERSION}.zip"
    unzip -q "$opencv_contrib_zip" -d "$opencv_src_root"
    cmake_args+=("-DOPENCV_EXTRA_MODULES_PATH=$opencv_contrib_dir/modules")
  fi

  mkdir -p "$opencv_build_dir"
  cmake -S "$opencv_src_dir" -B "$opencv_build_dir" "${cmake_args[@]}"
  cmake --build "$opencv_build_dir" --parallel "$(nproc)"
  run_as_root cmake --install "$opencv_build_dir"
  run_as_root ldconfig
}

ensure_opencv_required_version() {
  local installed
  installed="$(get_opencv_version)"
  local local_pkg_version
  local_pkg_version="$(get_local_opencv_pkgconfig_version)"

  local local_pkg_ok="true"
  if is_truthy "$OPENCV_REQUIRE_LOCAL_PKGCONFIG"; then
    if ! opencv_matches_required_version "$local_pkg_version"; then
      local_pkg_ok="false"
    fi
  fi

  if opencv_matches_required_version "$installed" && [[ "$local_pkg_ok" == "true" ]]; then
    log "OpenCV version $installed already available"
    activate_opencv_environment
    return
  fi

  if [[ -n "$installed" ]]; then
    warn "Detected OpenCV version $installed, but required is $OPENCV_REQUIRED_VERSION"
  else
    warn "No usable OpenCV installation detected"
  fi

  if is_truthy "$OPENCV_REQUIRE_LOCAL_PKGCONFIG"; then
    if [[ -n "$local_pkg_version" ]]; then
      warn "Local pkg-config reports OpenCV $local_pkg_version; expected $OPENCV_REQUIRED_VERSION"
    else
      warn "Local pkg-config metadata for opencv4 not found under $OPENCV_INSTALL_PREFIX"
    fi
  fi

  build_and_install_opencv_required_version

  activate_opencv_environment

  local final_version
  final_version="$(get_opencv_version)"
  opencv_matches_required_version "$final_version" || fail "OpenCV version check failed after install. Found '$final_version', expected '$OPENCV_REQUIRED_VERSION'"

  if is_truthy "$OPENCV_REQUIRE_LOCAL_PKGCONFIG"; then
    local final_local_pkg_version
    final_local_pkg_version="$(get_local_opencv_pkgconfig_version)"
    opencv_matches_required_version "$final_local_pkg_version" || fail "OpenCV local pkg-config check failed after install. Found '$final_local_pkg_version', expected '$OPENCV_REQUIRED_VERSION'"
  fi

  log "OpenCV $final_version is ready"
}

activate_local_libxisf_environment() {
  local multiarch
  multiarch="$(dpkg-architecture -qDEB_HOST_MULTIARCH 2>/dev/null || true)"

  local local_pkg_paths=()
  local_pkg_paths+=("$LIBXISF_INSTALL_PREFIX/lib/pkgconfig")
  local_pkg_paths+=("$LIBXISF_INSTALL_PREFIX/lib64/pkgconfig")
  if [[ -n "$multiarch" ]]; then
    local_pkg_paths+=("$LIBXISF_INSTALL_PREFIX/lib/$multiarch/pkgconfig")
  fi

  local existing_pkg_path="${PKG_CONFIG_PATH:-}"
  local combined_pkg_path="$existing_pkg_path"
  local pkg_path
  for pkg_path in "${local_pkg_paths[@]}"; do
    if [[ -d "$pkg_path" ]]; then
      combined_pkg_path="$pkg_path${combined_pkg_path:+:$combined_pkg_path}"
    fi
  done
  if [[ -n "$combined_pkg_path" ]]; then
    export PKG_CONFIG_PATH="$combined_pkg_path"
  fi

  local local_lib_paths=()
  local_lib_paths+=("$LIBXISF_INSTALL_PREFIX/lib")
  local_lib_paths+=("$LIBXISF_INSTALL_PREFIX/lib64")
  if [[ -n "$multiarch" ]]; then
    local_lib_paths+=("$LIBXISF_INSTALL_PREFIX/lib/$multiarch")
  fi

  local existing_ld="${LD_LIBRARY_PATH:-}"
  local combined_ld="$existing_ld"
  local lib_path
  for lib_path in "${local_lib_paths[@]}"; do
    if [[ -d "$lib_path" ]]; then
      combined_ld="$lib_path${combined_ld:+:$combined_ld}"
    fi
  done
  if [[ -n "$combined_ld" ]]; then
    export LD_LIBRARY_PATH="$combined_ld"
  fi

  export CMAKE_PREFIX_PATH="$LIBXISF_INSTALL_PREFIX:${CMAKE_PREFIX_PATH:-}"
  export CMAKE_INCLUDE_PATH="$LIBXISF_INSTALL_PREFIX/include:${CMAKE_INCLUDE_PATH:-}"

  local local_cmake_library_path="${LIBXISF_INSTALL_PREFIX}/lib"
  if [[ -n "$multiarch" ]]; then
    local_cmake_library_path="$local_cmake_library_path:${LIBXISF_INSTALL_PREFIX}/lib/$multiarch"
  fi
  local_cmake_library_path="$local_cmake_library_path:${LIBXISF_INSTALL_PREFIX}/lib64"
  export CMAKE_LIBRARY_PATH="$local_cmake_library_path:${CMAKE_LIBRARY_PATH:-}"
}

get_libxisf_header_candidate() {
  local candidate_paths=()
  candidate_paths+=("$LIBXISF_INSTALL_PREFIX/include/libxisf.h")
  candidate_paths+=("/usr/local/include/libxisf.h")
  candidate_paths+=("/usr/include/libxisf.h")

  local header
  for header in "${candidate_paths[@]}"; do
    if [[ -f "$header" ]]; then
      echo "$header"
      return
    fi
  done
}

libxisf_supports_required_api() {
  local header
  header="$(get_libxisf_header_candidate || true)"
  [[ -n "$header" && -f "$header" ]] || return 1

  grep -q 'CompressionCodecSupported' "$header" || return 1
  grep -q 'ZSTD' "$header" || return 1
}

build_and_install_libxisf() {
  log "Building LibXISF from source"

  rm -rf "$LIBXISF_WORKDIR"
  mkdir -p "$(dirname "$LIBXISF_WORKDIR")"

  git clone --depth 1 --branch "$LIBXISF_BRANCH" "$LIBXISF_REPO_URL" "$LIBXISF_WORKDIR"

  cmake -S "$LIBXISF_WORKDIR" -B "$LIBXISF_WORKDIR/build" \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_INSTALL_PREFIX="$LIBXISF_INSTALL_PREFIX" \
    -DUSE_BUNDLED_LIBS=ON \
    -DBUILD_SHARED_LIBS=ON

  cmake --build "$LIBXISF_WORKDIR/build" --parallel "$(nproc)"
  run_as_root cmake --install "$LIBXISF_WORKDIR/build"
  run_as_root ldconfig

  activate_local_libxisf_environment
}

ensure_modern_libxisf_if_enabled() {
  if ! is_truthy "$INDI_ENABLE_XISF"; then
    return
  fi

  activate_local_libxisf_environment

  if libxisf_supports_required_api; then
    local detected_header
    detected_header="$(get_libxisf_header_candidate || true)"
    log "LibXISF API is compatible: $detected_header"
    return
  fi

  if ! is_truthy "$LIBXISF_AUTO_UPGRADE_IF_OLD"; then
    fail "LibXISF is too old for INDI XISF support; set LIBXISF_AUTO_UPGRADE_IF_OLD=true or disable XISF"
  fi

  warn "Detected LibXISF without required ZSTD API; upgrading from source"
  build_and_install_libxisf

  if ! libxisf_supports_required_api; then
    fail "LibXISF source upgrade completed but required API is still missing"
  fi

  local upgraded_header
  upgraded_header="$(get_libxisf_header_candidate || true)"
  log "LibXISF upgraded and validated: $upgraded_header"
}

stage_opencvsharp_runtime_from_nuget() {
  log "Staging OpenCvSharp Linux runtime from NuGet"

  [[ "$TARGET_RUNTIME" == "linux-x64" ]] || {
    warn "Skipping OpenCvSharp runtime staging for TARGET_RUNTIME=$TARGET_RUNTIME"
    return
  }

  local runtime_version
  runtime_version="$(sed -n 's/.*OpenCvSharp4\.official\.runtime\.\$(RuntimeIdentifier)" Version="\([^"]*\)".*/\1/p' NINA/NINA.csproj | head -n 1)"
  [[ -n "$runtime_version" ]] || fail "Could not parse OpenCvSharp runtime package version from NINA/NINA.csproj"

  local nuget_global_packages
  nuget_global_packages="$(dotnet nuget locals global-packages --list | sed -n 's/^global-packages: //p' | head -n 1)"
  [[ -n "$nuget_global_packages" ]] || fail "Could not determine NuGet global-packages path"

  local package_root="$nuget_global_packages/${OPENCVSHARP_RUNTIME_PACKAGE,,}/$runtime_version"
  local package_so="$package_root/runtimes/linux-x64/native/libOpenCvSharpExtern.so"

  if [[ ! -f "$package_so" ]]; then
    log "OpenCvSharp runtime package not found in cache, restoring NINA/NINA.csproj for $TARGET_RUNTIME"
    dotnet restore NINA/NINA.csproj -r "$TARGET_RUNTIME"
  fi

  [[ -f "$package_so" ]] || fail "OpenCvSharp runtime library not found: $package_so"

  local stage_dir="NINA/External/$TARGET_RUNTIME"
  mkdir -p "$stage_dir"
  cp -f "$package_so" "$stage_dir/libOpenCvSharpExtern.so"
  chmod 755 "$stage_dir/libOpenCvSharpExtern.so"

  log "Staged OpenCvSharp runtime library at: $stage_dir/libOpenCvSharpExtern.so"
}

install_dotnet_10() {
  local dotnet_ok="false"

  if command -v dotnet >/dev/null 2>&1; then
    if dotnet --list-sdks | grep -q '^10\.'; then
      dotnet_ok="true"
    fi
  fi

  if [[ "$dotnet_ok" == "true" ]]; then
    log ".NET 10 SDK already available"
    return
  fi

  log "Installing .NET 10 SDK"
  source /etc/os-release
  local ms_repo_deb
  ms_repo_deb="$(mktemp)"

  if ! wget -q "https://packages.microsoft.com/config/ubuntu/${VERSION_ID}/packages-microsoft-prod.deb" -O "$ms_repo_deb"; then
    fail "Failed to download Microsoft package feed for Ubuntu ${VERSION_ID}"
  fi

  run_as_root dpkg -i "$ms_repo_deb"
  rm -f "$ms_repo_deb"

  run_as_root apt-get update
  run_as_root apt-get install -y dotnet-sdk-10.0
}

install_node_22() {
  local node_ok="false"

  if command -v node >/dev/null 2>&1; then
    local node_major
    node_major="$(node -p 'process.versions.node.split(".")[0]')"
    if [[ "$node_major" == "22" ]]; then
      node_ok="true"
    fi
  fi

  if [[ "$node_ok" == "true" ]]; then
    log "Node.js 22 already available"
    return
  fi

  log "Installing Node.js 22"
  local nodesource_script
  nodesource_script="$(mktemp)"
  curl -fsSL https://deb.nodesource.com/setup_22.x -o "$nodesource_script"
  run_as_root bash "$nodesource_script"
  rm -f "$nodesource_script"

  run_as_root apt-get install -y nodejs
}

print_tool_versions() {
  log "Toolchain versions"
  dotnet --info
  node --version
  npm --version
}

update_submodules_except_external() {
  log "Updating git submodules (excluding NINA/External)"

  git lfs install
  # a checkout made before git-lfs was installed still holds pointer files
  git lfs pull

  local excluded_path="NINA/External"
  while IFS= read -r key; do
    local path
    path="$(git config -f .gitmodules --get "$key" || true)"
    # Normalize CRLF and trim whitespace to avoid empty/invalid pathspecs.
    path="${path//$'\r'/}"
    path="${path#${path%%[![:space:]]*}}"
    path="${path%${path##*[![:space:]]}}"

    if [[ -z "$path" ]]; then
      warn "Skipping submodule entry with empty path for key: $key"
      continue
    fi

    if [[ "$path" == "$excluded_path" ]]; then
      log "Skipping submodule $path"
      continue
    fi

    log "Updating submodule $path"
    if git submodule update --init --recursive --remote "$path"; then
      continue
    fi

    warn "Remote update failed for $path; falling back to pinned commit"
    git submodule update --init --recursive "$path"
  done < <(git config -f .gitmodules --name-only --get-regexp '^submodule\..*\.path$' || true)
}

clone_repo_fresh() {
  local url="$1"
  local dir="$2"
  local branch="${3:-}"

  rm -rf "$dir"
  if [[ -n "$branch" ]]; then
    git clone --branch "$branch" --single-branch "$url" "$dir"
  else
    git clone "$url" "$dir"
  fi
}

clone_workflow_plugin_repositories() {
  log "Cloning plugin repositories used by workflow"

  mkdir -p NINA.Plugins

  clone_repo_fresh "https://github.com/nitr57/N.I.N.A-Plugin-for-Touch-N-Stars" "NINA.Plugins/Touch-N-Stars" "develop"
  clone_repo_fresh "https://github.com/nitr57/joko.nina.plugins" "NINA.Plugins/joko.nina.plugins"
  clone_repo_fresh "https://github.com/nitr57/ninaAPI" "NINA.Plugins/ninaAPI"
  clone_repo_fresh "https://github.com/nitr57/nina.plugin.phd2tools" "NINA.Plugins/nina.plugin.phd2tools"
  clone_repo_fresh "https://github.com/nitr57/nina.plugin.orbuculum" "NINA.Plugins/nina.plugin.orbuculum"
  clone_repo_fresh "https://github.com/nitr57/nina.plugin.polaralignment" "NINA.Plugins/PolarAlignment"
  clone_repo_fresh "https://github.com/nitr57/nina.plugin.livestack" "NINA.Plugins/LiveStack"
  clone_repo_fresh "https://github.com/nitr57/NINA.Joko.Plugin.TenMicron" "NINA.Plugins/NINA.Joko.Plugin.TenMicron"
  clone_repo_fresh "https://github.com/nitr57/pins.plugin" "NINA.Plugins/pins.plugin"
}

build_and_install_libgpiod() {
  log "Building and installing libgpiod 2.2"

  local tmp_dir
  tmp_dir="$(mktemp -d)"

  pushd "$tmp_dir" >/dev/null
  wget https://mirrors.edge.kernel.org/pub/software/libs/libgpiod/libgpiod-2.2.tar.gz
  tar -xzf libgpiod-2.2.tar.gz
  pushd libgpiod-2.2 >/dev/null
  ./configure --prefix=/usr --enable-tools=yes --enable-bindings-cxx
  make -j"$(nproc)"
  run_as_root make install
  run_as_root ldconfig
  popd >/dev/null
  popd >/dev/null

  rm -rf "$tmp_dir"
}

populate_external_bundle() {
  log "Populating NINA/External from repository clone and source builds"

  local external_repo_url="https://github.com/nitr57/pins.external"
  local external_repo_branch="main"
  local jpleph_url="https://github.com/isbeorn/nina.external/raw/refs/heads/master/JPLEPH"
  local runtime_dir="NINA/External/$TARGET_RUNTIME"

  rm -rf "NINA/External"
  git clone --depth 1 --branch "$external_repo_branch" "$external_repo_url" "NINA/External"

  pushd "NINA/External" >/dev/null
  git lfs pull || true
  popd >/dev/null

  curl -L --fail --retry 3 --retry-delay 5 -o "NINA/External/JPLEPH" "$jpleph_url"
  rm -f "NINA/External/.gitignore" "NINA/External/.gitattributes"

  [[ -d "$runtime_dir" ]] || fail "$runtime_dir not found after repository clone"

  # Rebuild NOVAS and sync generated files into the runtime-specific external layout.
  pushd "NOVAS31" >/dev/null
  make clean || true
  make
  popd >/dev/null

  mkdir -p "$runtime_dir/NOVAS"
  cp -f "NOVAS31/libnovas_c.so" "$runtime_dir/NOVAS/libnovas_c.so"
  cp -f "NOVAS31/cio_ra.bin" "$runtime_dir/NOVAS/cio_ra.bin"

  [[ -f "NINA/External/JPLEPH" ]] || fail "NINA/External/JPLEPH not found"

  # SOFA makefile builds a static library by default, so build a shared object explicitly.
  pushd "SOFA/SOFA/src" >/dev/null
  make clean || true
  make
  rm -f ./*.o libsofa_c.so
  local src
  for src in ./*.c; do
    if [[ "$(basename "$src")" == "t_sofa_c.c" ]]; then
      continue
    fi
    gcc -fPIC -O2 -c "$src"
  done
  gcc -shared -o libsofa_c.so ./*.o -lm
  popd >/dev/null

  mkdir -p "$runtime_dir/SOFA"
  cp -f "SOFA/SOFA/src/libsofa_c.so" "$runtime_dir/SOFA/libsofa_c.so"

  # Keep only NOVAS and SOFA under the runtime-specific external folder.
  find "$runtime_dir" -mindepth 1 -maxdepth 1 ! -name 'NOVAS' ! -name 'SOFA' -exec rm -rf {} +

  log "External bundle populated from repository clone into NINA/External"
}

determine_versions() {
  log "Determining package versions"

  BASE_VERSION="$(grep -m1 'AssemblyInformationalVersion' CommonAssemblyInfo.cs | sed -E 's/.*"([^"]+)".*/\1/')"
  [[ -n "$BASE_VERSION" ]] || fail "Failed to determine version from CommonAssemblyInfo.cs"

  BASE_VERSION="${BASE_VERSION/-canary/-pins-canary}"
  PACKAGE_VERSION="${BASE_VERSION}+${BUILD_NUMBER}"

  log "Base version: $BASE_VERSION"
  log "Package version: $PACKAGE_VERSION"
  log "Release version: $RELEASE_VERSION"
}

build_core_pins_package() {
  log "Building PINS core for $TARGET_RUNTIME"

  local publish_root="$PUBLISH_DIRECTORY/$TARGET_RUNTIME"
  local host_multiarch
  host_multiarch="$(dpkg-architecture -qDEB_HOST_MULTIARCH 2>/dev/null || true)"

  dotnet restore NINA/NINA.csproj -r "$TARGET_RUNTIME"
  dotnet restore System.Windows.Compat/System.Windows.Compat.csproj

  dotnet build System.Windows.Compat/System.Windows.Compat.csproj \
    -c "$BUILD_CONFIGURATION" \
    --no-restore

  dotnet build NINA/NINA.csproj \
    -c "$BUILD_CONFIGURATION" \
    -r "$TARGET_RUNTIME" \
    --no-restore

  dotnet publish NINA/NINA.csproj \
    -c "$BUILD_CONFIGURATION" \
    -r "$TARGET_RUNTIME" \
    --no-build \
    -o "$publish_root"

  [[ -d "NINA/External" ]] || fail "NINA/External not found"
  mkdir -p "$publish_root/External"
  rsync -a --exclude='.git' "NINA/External/" "$publish_root/External/"

  local opencv_target="$publish_root/OpenCvSharpExtern.so"
  local expected_opencv_so="$publish_root/External/$TARGET_RUNTIME/libOpenCvSharpExtern.so"
  local source_opencv_so=""

  if [[ -f "$opencv_target" ]]; then
    source_opencv_so="$opencv_target"
  elif [[ -f "$expected_opencv_so" ]]; then
    source_opencv_so="$expected_opencv_so"
  else
    source_opencv_so="$(find "$publish_root" -type f -name 'libOpenCvSharpExtern.so' | head -n 1 || true)"
  fi

  if [[ -n "$source_opencv_so" && -f "$source_opencv_so" ]]; then
    if [[ "$source_opencv_so" != "$opencv_target" ]]; then
      cp -f "$source_opencv_so" "$opencv_target"
      log "Using OpenCvSharp native library from: $source_opencv_so"
    fi
    chmod 755 "$opencv_target"
  else
    fail "OpenCvSharp native library not found in publish output (expected libOpenCvSharpExtern.so)"
  fi

  rm -rf "$PACKAGE_ROOT"
  mkdir -p "$PACKAGE_ROOT/pins"
  rsync -a "$publish_root/" "$PACKAGE_ROOT/pins/"
  echo "$INSTALL_DIRECTORY" > "$PACKAGE_ROOT/pins/.install_path"

  local compat_dll="System.Windows.Compat/bin/$BUILD_CONFIGURATION/net10.0/System.Windows.dll"
  [[ -f "$compat_dll" ]] || fail "Compat build missing at $compat_dll"
  cp -f "$compat_dll" "$PACKAGE_ROOT/pins/System.Windows.dll"

  local deb_root="$DEB_ROOT"
  local install_root="$deb_root/${INSTALL_DIRECTORY#/}"
  local control_dir="$deb_root/DEBIAN"

  rm -rf "$deb_root"
  mkdir -p "$install_root" "$control_dir"
  rsync -a "$PACKAGE_ROOT/pins/" "$install_root/"

  local service_path="$deb_root/etc/systemd/system/pins.service"
  mkdir -p "$(dirname "$service_path")"
  cp packaging/systemd/pins.service "$service_path"
  sed -i \
    -e "s|/home/pi/pins|$INSTALL_DIRECTORY|g" \
    -e "s|User=pi|User=$TARGET_USER|g" \
    -e "s|Group=pi|Group=$TARGET_USER|g" \
    "$service_path"
  if [[ -n "$host_multiarch" ]]; then
    sed -i -e "s|aarch64-linux-gnu|$host_multiarch|g" "$service_path"
  fi
  chmod 644 "$service_path"

  cp packaging/debian/postinst "$control_dir/postinst"
  cp packaging/debian/prerm "$control_dir/prerm"
  sed -i \
    -e "s|chown -R pi:pi|chown -R $TARGET_USER:$TARGET_USER|g" \
    -e "s|/home/pi/pins|$INSTALL_DIRECTORY|g" \
    "$control_dir/postinst"
  chmod 755 "$control_dir/postinst" "$control_dir/prerm"

  local installed_size
  installed_size="$(du -sk "$install_root" | cut -f1)"

  {
    echo "Package: $DEB_PACKAGE"
    echo "Version: $PACKAGE_VERSION"
    echo "Section: misc"
    echo "Priority: optional"
    echo "Architecture: $DEB_ARCH"
    echo "Maintainer: PINS Team"
    echo "Installed-Size: $installed_size"
    echo "Description: Pins build for Ubuntu x64"
  } > "$control_dir/control"

  local deb_path="artifacts/${DEB_PACKAGE}_${PACKAGE_VERSION}_${DEB_ARCH}_${RELEASE_DATE}.deb"
  dpkg-deb --root-owner-group --build "$deb_root" "$deb_path"
  sha256sum "$deb_path" > "$deb_path.sha256"
  record_built_deb "$deb_path"

  CORE_EXCLUDES_FILE="artifacts/core_excludes.txt"
  mkdir -p artifacts
  (
    cd "$publish_root"
    find . -type f -printf "%P\n"
  ) > "$CORE_EXCLUDES_FILE"

  log "Built core package: $deb_path"
}

build_pinsdaemon_package() {
  log "Building pinsdaemon package"

  rm -rf pinsdaemon
  git clone https://github.com/Touch-N-Stars/pinsdaemon pinsdaemon

  pushd pinsdaemon >/dev/null
  mkdir -p build/opt/pinsdaemon
  mkdir -p build/usr/local/bin
  mkdir -p build/etc/systemd/system
  mkdir -p build/etc/sudoers.d
  mkdir -p build/home/$TARGET_USER/pins-scripts
  mkdir -p build/lib/udev/rules.d
  mkdir -p build/DEBIAN

  cp -r app build/opt/pinsdaemon/
  cp requirements.txt build/opt/pinsdaemon/
  cp README.md build/opt/pinsdaemon/

  cp scripts/system-upgrade.sh build/usr/local/bin/
  cp scripts/manage-samba.sh build/usr/local/bin/
  cp scripts/wifi-connect.sh build/usr/local/bin/
  cp scripts/wifi-automanage.py build/usr/local/bin/
  cp scripts/wifi-scan.py build/usr/local/bin/
  cp scripts/install-firmware.sh build/usr/local/bin/
  cp scripts/install-indi-package.sh build/usr/local/bin/
  cp scripts/ensure-astap-data-links.sh build/usr/local/bin/

  cp scripts/hotspot.sh build/home/$TARGET_USER/pins-scripts/

  cp systemd/sysupdate-api.service build/etc/systemd/system/
  cp packaging/zz-pins.rules build/lib/udev/rules.d/zz-pins.rules
  cp packaging/sudoers build/etc/sudoers.d/sysupdate-api

  cp packaging/DEBIAN/* build/DEBIAN/
  chmod 755 build/DEBIAN/postinst
  chmod 755 build/DEBIAN/prerm

  local base_version
  base_version="$(grep '^Version:' packaging/DEBIAN/control | awk '{print $2}')"
  [[ -n "$base_version" ]] || fail "Unable to parse pinsdaemon base version"

  local full_version="${base_version}-${BUILD_NUMBER}"
  sed -i -E "s/^Version:.*/Version: ${full_version}/" build/DEBIAN/control
  sed -i -E "s/^Architecture:.*/Architecture: ${DEB_ARCH}/" build/DEBIAN/control

  local deb_path="../artifacts/pinsdaemon_${full_version}_${DEB_ARCH}.deb"
  dpkg-deb --root-owner-group --build build "$deb_path"
  sha256sum "$deb_path" > "$deb_path.sha256"
  record_built_deb "$deb_path"

  popd >/dev/null

  log "Built pinsdaemon package"
}

build_wandereretasdk_package() {
  log "Building WandererETASDK package"

  rm -rf WandererETASDK
  git clone https://github.com/nitr57/WandererETASDK WandererETASDK

  pushd WandererETASDK >/dev/null
  mkdir -p build
  pushd build >/dev/null
  cmake ..
  make -j"$(nproc)"
  popd >/dev/null
  popd >/dev/null

  local deb_package="pins-wandereretasdk"
  local deb_root="artifacts/wandereretasdk-debroot"
  local install_root="$deb_root/usr/lib"
  local control_dir="$deb_root/DEBIAN"

  rm -rf "$deb_root"
  mkdir -p "$install_root" "$control_dir"

  mapfile -t lib_files < <(find "WandererETASDK/build" -type f \( -name 'lib*.so' -o -name 'lib*.so.*' \))
  [[ ${#lib_files[@]} -gt 0 ]] || fail "No shared libraries found in WandererETASDK/build"

  cp -a "${lib_files[@]}" "$install_root/"

  local installed_size
  installed_size="$(du -sk "$deb_root/usr" | cut -f1)"

  {
    echo "Package: $deb_package"
    echo "Version: $PACKAGE_VERSION"
    echo "Section: libs"
    echo "Priority: optional"
    echo "Architecture: $DEB_ARCH"
    echo "Maintainer: PINS Team"
    echo "Installed-Size: $installed_size"
    echo "Description: WandererETA SDK shared library package"
  } > "$control_dir/control"

  local deb_path="artifacts/${deb_package}_${PACKAGE_VERSION}_${DEB_ARCH}_${RELEASE_DATE}.deb"
  dpkg-deb --root-owner-group --build "$deb_root" "$deb_path"
  sha256sum "$deb_path" > "$deb_path.sha256"
  record_built_deb "$deb_path"

  log "Built WandererETASDK package"
}

find_assembly_info() {
  local plugin_dir="$1"
  local assembly_info="$plugin_dir/Properties/AssemblyInfo.cs"

  if [[ -f "$assembly_info" ]]; then
    echo "$assembly_info"
    return
  fi

  local discovered
  discovered="$(find "$plugin_dir" -name AssemblyInfo.cs | head -n 1 || true)"
  [[ -n "$discovered" ]] || return 1
  echo "$discovered"
}

build_plugin_deb() {
  local project_path="$1"
  local install_folder="$2"
  local deb_package="$3"
  local needs_java="${4:-false}"

  local plugin_dir
  plugin_dir="$(dirname "$project_path")"

  local assembly_info
  assembly_info="$(find_assembly_info "$plugin_dir")" || fail "AssemblyInfo.cs not found for $deb_package"

  local assembly_version_line
  assembly_version_line="$(grep -E '^\s*\[assembly:\s*AssemblyVersion' "$assembly_info" || true)"
  if [[ -z "$assembly_version_line" ]]; then
    assembly_version_line="$(grep -E 'AssemblyVersion' "$assembly_info" || true)"
  fi

  local plugin_version
  plugin_version="$(echo "$assembly_version_line" | sed -E 's/.*"([^"]+)".*/\1/')"
  [[ -n "$plugin_version" ]] || fail "AssemblyVersion not found in $assembly_info"
  plugin_version="${plugin_version}+${BUILD_NUMBER}"

  dotnet restore "$project_path" -r "$TARGET_RUNTIME"

  if [[ "$needs_java" == "true" ]]; then
    local java_path
    java_path="$(command -v java || true)"
    JavaPath="$java_path" dotnet build "$project_path" \
      -c "$BUILD_CONFIGURATION" \
      -f "$PLUGIN_TARGET_FRAMEWORK" \
      -r "$TARGET_RUNTIME" \
      --no-restore \
      "${PLUGIN_MSBUILD_ARGS[@]}"
  else
    dotnet build "$project_path" \
      -c "$BUILD_CONFIGURATION" \
      -f "$PLUGIN_TARGET_FRAMEWORK" \
      -r "$TARGET_RUNTIME" \
      --no-restore \
      "${PLUGIN_MSBUILD_ARGS[@]}"
  fi

  local tfm_dir="$plugin_dir/bin/$BUILD_CONFIGURATION/$PLUGIN_TARGET_FRAMEWORK/$TARGET_RUNTIME"
  if [[ ! -d "$tfm_dir" ]]; then
    local build_root="$plugin_dir/bin/$BUILD_CONFIGURATION"
    tfm_dir="$(find "$build_root" -maxdepth 1 -type d -name 'net*' | head -n 1 || true)"
    [[ -n "$tfm_dir" ]] || fail "Could not locate target framework output under $build_root"
  fi

  local deb_root="$PLUGIN_DEB_BASE/$deb_package"
  local install_root="$deb_root$PLUGINS_INSTALL_BASE/$install_folder"
  local control_dir="$deb_root/DEBIAN"

  rm -rf "$deb_root"
  mkdir -p "$install_root" "$control_dir"

  [[ -f "$CORE_EXCLUDES_FILE" ]] || fail "Core exclude list not found: $CORE_EXCLUDES_FILE"
  rsync -a --exclude-from="$CORE_EXCLUDES_FILE" "$tfm_dir/" "$install_root/"

  find "$install_root" -mindepth 1 -maxdepth 1 -type d -name '??-??' -exec rm -rf {} +
  find "$install_root" -mindepth 1 -maxdepth 1 -type d -name '??' -exec rm -rf {} +

  local extra_libs="$plugin_dir/extra-libs"
  if [[ -d "$extra_libs" ]]; then
    find "$extra_libs" -maxdepth 1 -type f -name '*.dll' -exec cp {} "$install_root/" \;
  fi

  local app_dir="$plugin_dir/app"
  if [[ -d "$app_dir" ]]; then
    rm -rf "$install_root/app"
    cp -a "$app_dir" "$install_root/app"
  fi

  local installed_size
  installed_size="$(du -sk "$deb_root$PLUGINS_INSTALL_BASE" | cut -f1)"

  {
    echo "Package: $deb_package"
    echo "Version: $plugin_version"
    echo "Section: misc"
    echo "Priority: optional"
    echo "Architecture: $DEB_ARCH"
    echo "Maintainer: PINS Team"
    echo "Installed-Size: $installed_size"
    echo "Description: NINA plugin package for $install_folder"
  } > "$control_dir/control"

  {
    echo "#!/bin/sh"
    echo "set -e"
    echo "chown $TARGET_USER:$TARGET_USER \"$(dirname "$PLUGINS_INSTALL_BASE")\" || true"
    echo "chown $TARGET_USER:$TARGET_USER \"$PLUGINS_INSTALL_BASE\" || true"
    echo "chown -R $TARGET_USER:$TARGET_USER \"$PLUGINS_INSTALL_BASE/$install_folder\" || true"
    echo "exit 0"
  } > "$control_dir/postinst"
  chmod 755 "$control_dir/postinst"

  local deb_path="$PLUGIN_ARTIFACT_DIR/${deb_package}_${plugin_version}_${DEB_ARCH}_${RELEASE_DATE}.deb"
  dpkg-deb --root-owner-group --build "$deb_root" "$deb_path"
  sha256sum "$deb_path" > "$deb_path.sha256"
  record_built_deb "$deb_path"
}

prepare_touch_n_stars_frontend() {
  local project_path="NINA.Plugins/Touch-N-Stars/Touch-N-Stars/Touch-N-Stars.csproj"
  local plugin_dir
  plugin_dir="$(dirname "$project_path")"
  local tmp_dir="$plugin_dir/frontend-build"

  rm -rf "$tmp_dir" "$plugin_dir/app"
  git clone --branch develop --single-branch https://github.com/Touch-N-Stars/Touch-N-Stars.git "$tmp_dir"

  pushd "$tmp_dir" >/dev/null
  npm install
  npm run build
  popd >/dev/null

  mv "$tmp_dir/dist" "$plugin_dir/app"
  rm -rf "$tmp_dir"
}

prepare_pins_plugin_sdks() {
  local plugin_dir="NINA.Plugins/pins.plugin"
  local plugin_dir_abs="$ROOT_DIR/$plugin_dir"

  mkdir -p "$plugin_dir_abs/extra-libs"

  local powerbox_sdk_tmp
  powerbox_sdk_tmp="$(mktemp -d)"
  git clone --recursive https://github.com/nitr57/pins.powerbox.sdk "$powerbox_sdk_tmp"
  pushd "$powerbox_sdk_tmp" >/dev/null
  mkdir -p build
  pushd build >/dev/null
  cmake ..
  make -j"$(nproc)"
  find . -name 'libPowerBoxSDK.so' -exec cp {} "$plugin_dir_abs/extra-libs/PowerBoxSDK.dll" \;
  popd >/dev/null
  popd >/dev/null
  rm -rf "$powerbox_sdk_tmp"

  [[ -f "$plugin_dir_abs/extra-libs/PowerBoxSDK.dll" ]] || fail "PowerBoxSDK.dll was not created"

  local meteostation_sdk_tmp
  meteostation_sdk_tmp="$(mktemp -d)"
  git clone --recursive https://github.com/nitr57/pins.meteostation.sdk "$meteostation_sdk_tmp"
  pushd "$meteostation_sdk_tmp" >/dev/null
  mkdir -p build
  pushd build >/dev/null
  cmake ..
  make -j"$(nproc)"
  find . -name 'libMeteoStationSDK.so' -exec cp {} "$plugin_dir_abs/extra-libs/MeteoStationSDK.dll" \;
  popd >/dev/null
  popd >/dev/null
  rm -rf "$meteostation_sdk_tmp"

  [[ -f "$plugin_dir_abs/extra-libs/MeteoStationSDK.dll" ]] || fail "MeteoStationSDK.dll was not created"
}

build_plugins() {
  log "Building plugin Debian packages"

  rm -rf "$PLUGIN_DEB_BASE"
  mkdir -p "$PLUGIN_ARTIFACT_DIR"
  mkdir -p "$(dirname "$PLUGIN_FAILURES_FILE")"
  rm -f "$PLUGIN_FAILURES_FILE"

  build_plugin_deb \
    "NINA.Plugins/joko.nina.plugins/Joko.NINA.Plugins/Joko.NINA.Plugins.HocusFocus/Joko.NINA.Plugins.HocusFocus.csproj" \
    "joko.nina.plugins" \
    "pins-plugin-joko"

  build_plugin_deb \
    "NINA.Plugins/NINA.Joko.Plugin.TenMicron/NINA.Joko.Plugin.TenMicron/NINA.Joko.Plugin.TenMicron.csproj" \
    "NINA.Joko.Plugin.TenMicron" \
    "pins-plugin-tenmicron" \
    "true"

  build_plugin_deb \
    "NINA.Plugins/LiveStack/nina.plugin.livestack.csproj" \
    "LiveStack" \
    "pins-plugin-livestack"

  build_plugin_deb \
    "NINA.Plugins/ninaAPI/ninaAPI/ninaAPI.csproj" \
    "ninaAPI" \
    "pins-plugin-ninaapi"

  build_plugin_deb \
    "NINA.Plugins/nina.plugin.orbuculum/Orbuculum/Orbuculum.csproj" \
    "Orbuculum" \
    "pins-plugin-orbuculum"

  build_plugin_deb \
    "NINA.Plugins/nina.plugin.phd2tools/nina.plugin.phd2tools.csproj" \
    "Phd2 Tools" \
    "pins-plugin-phd2tools"

  build_plugin_deb \
    "NINA.Plugins/PolarAlignment/PolarAlignment/NINA.Plugins.PolarAlignment.csproj" \
    "PolarAlignment" \
    "pins-plugin-polaralignment"

  prepare_touch_n_stars_frontend
  build_plugin_deb \
    "NINA.Plugins/Touch-N-Stars/Touch-N-Stars/Touch-N-Stars.csproj" \
    "Touch-N-Stars" \
    "pins-plugin-touch-n-stars"

  prepare_pins_plugin_sdks
  build_plugin_deb \
    "NINA.Plugins/pins.plugin/PI'N'Stars.csproj" \
    "pins.plugin" \
    "pins-plugin-pins"

  log "All plugin packages built successfully"
}

build_indi_debian_packages() {
  log "Building INDI Debian packages"

  ensure_modern_libxisf_if_enabled

  local indi_src="artifacts/src/indi"
  rm -rf "$indi_src"
  mkdir -p "$(dirname "$indi_src")"

  git clone --branch "v$INDI_VERSION" --depth 1 https://github.com/indilib/indi.git "$indi_src"

  local indi_xisf_args=()
  if ! is_truthy "$INDI_ENABLE_XISF"; then
    # LibXISF in distro repos can be older than what INDI expects; disable optional XISF support by default.
    indi_xisf_args+=(
      -DCMAKE_DISABLE_FIND_PACKAGE_LibXISF=TRUE
      -DINDI_WITH_XISF=OFF
      -DWITH_XISF=OFF
    )
  fi

  cmake -S "$indi_src" -B "$indi_src/build" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_INSTALL_PREFIX=/usr \
    -DUDEVRULES_INSTALL_DIR=/lib/udev/rules.d \
    -DINDI_BUILD_SERVER=ON \
    -DINDI_BUILD_CLIENT=ON \
    -DINDI_BUILD_DRIVERS=ON \
    -DINDI_BUILD_COMMON=ON \
    -DINDI_BUILD_QT_CLIENT=OFF \
    -DINDI_BUILD_UNITTESTS=OFF \
    -DINDI_BUILD_INTEGTESTS=OFF \
    -DINDI_BUILD_EXAMPLES=OFF \
    -DINDI_BUILD_STATIC=OFF \
    "${indi_xisf_args[@]}"

  cmake --build "$indi_src/build" --parallel "$(nproc)"

  local package_version="${INDI_VERSION}+${BUILD_NUMBER}"

  local deb_root_base="$INDI_DEB_ROOT"
  local runtime_root="$deb_root_base/runtime"
  local lib_root="$deb_root_base/lib"
  local data_root="$deb_root_base/data"
  local dev_root="$deb_root_base/dev"

  rm -rf "$deb_root_base"
  mkdir -p "$runtime_root/DEBIAN" "$lib_root/DEBIAN" "$data_root/DEBIAN" "$dev_root/DEBIAN"

  local full_stage_dir
  full_stage_dir="$(mktemp -d)"
  DESTDIR="$full_stage_dir" cmake --install "$indi_src/build"

  rsync -a "$full_stage_dir/" "$runtime_root/"

  rm -rf "$runtime_root/usr/include"
  rm -rf "$runtime_root/usr/lib/pkgconfig" "$runtime_root/usr/lib/cmake"

  local multiarch
  multiarch="$(dpkg-architecture -qDEB_HOST_MULTIARCH 2>/dev/null || true)"

  if [[ -n "$multiarch" ]]; then
    rm -rf "$runtime_root/usr/lib/$multiarch/pkgconfig" "$runtime_root/usr/lib/$multiarch/cmake"
  fi

  local lib_roots=("usr/lib")
  if [[ -n "$multiarch" ]]; then
    lib_roots+=("usr/lib/$multiarch")
  fi

  for librel in "${lib_roots[@]}"; do
    local runtime_dir="$runtime_root/$librel"
    local lib_dir="$lib_root/$librel"

    mkdir -p "$lib_dir"
    if [[ -d "$runtime_dir" ]]; then
      find "$runtime_dir" -maxdepth 1 -type f -name 'lib*.so.*' -exec cp -a {} "$lib_dir/" \;
      find "$runtime_dir" -maxdepth 1 -type f -name 'lib*.so.*' -delete
      find "$runtime_dir" -maxdepth 1 -type l -name 'lib*.so' -delete
      find "$runtime_dir" -maxdepth 1 -type f -name 'lib*.so' -delete
      find "$runtime_dir" -maxdepth 1 -type f -name 'lib*.a' -delete
    fi
  done

  local plugin_lib_roots=("usr/lib/indi")
  if [[ -n "$multiarch" ]]; then
    plugin_lib_roots+=("usr/lib/$multiarch/indi")
  fi

  for librel in "${plugin_lib_roots[@]}"; do
    local runtime_plugin_dir="$runtime_root/$librel"
    local lib_plugin_dir="$lib_root/$librel"

    if [[ -d "$runtime_plugin_dir" ]]; then
      mkdir -p "$lib_plugin_dir"
      rsync -a "$runtime_plugin_dir/" "$lib_plugin_dir/"
      rm -rf "$runtime_plugin_dir"
    fi
  done

  if [[ -d "$runtime_root/usr/share/indi" ]]; then
    mkdir -p "$data_root/usr/share/indi"
    rsync -a "$runtime_root/usr/share/indi/" "$data_root/usr/share/indi/"
    rm -rf "$runtime_root/usr/share/indi"
  fi

  local runtime_installed_size
  runtime_installed_size="$(du -sk "$runtime_root/usr" | cut -f1)"

  {
    echo "Package: indi-bin"
    echo "Version: $package_version"
    echo "Section: misc"
    echo "Priority: optional"
    echo "Architecture: $DEB_ARCH"
    echo "Maintainer: PINS Team"
    echo "Depends: libindi1 (= $package_version), libindi-data (= $package_version), libc6, libstdc++6"
    echo "Provides: indi-core"
    echo "Replaces: indi-bin (<< $INDI_VERSION), indi-core (<< $INDI_VERSION)"
    echo "Installed-Size: $runtime_installed_size"
    echo "Description: INDI core library, server, and drivers built from upstream v$INDI_VERSION"
  } > "$runtime_root/DEBIAN/control"

  if ! find "$lib_root/usr" -type f -name 'lib*.so.*' | grep -q .; then
    rm -rf "$full_stage_dir"
    fail "Expected runtime shared libraries were not staged for libindi1"
  fi

  local lib_installed_size
  lib_installed_size="$(du -sk "$lib_root/usr" | cut -f1)"

  {
    echo "Package: libindi1"
    echo "Version: $package_version"
    echo "Section: libs"
    echo "Priority: optional"
    echo "Architecture: $DEB_ARCH"
    echo "Maintainer: PINS Team"
    echo "Depends: libc6, libstdc++6"
    echo "Replaces: libindi1 (<< $INDI_VERSION)"
    echo "Installed-Size: $lib_installed_size"
    echo "Description: INDI runtime shared libraries built from upstream v$INDI_VERSION"
  } > "$lib_root/DEBIAN/control"

  if [[ ! -d "$data_root/usr/share/indi" ]]; then
    rm -rf "$full_stage_dir"
    fail "Expected INDI data files were not staged for libindi-data"
  fi

  local data_installed_size
  data_installed_size="$(du -sk "$data_root/usr" | cut -f1)"

  {
    echo "Package: libindi-data"
    echo "Version: $package_version"
    echo "Section: misc"
    echo "Priority: optional"
    echo "Architecture: $DEB_ARCH"
    echo "Maintainer: PINS Team"
    echo "Depends: libc6"
    echo "Replaces: libindi-data (<< $INDI_VERSION)"
    echo "Installed-Size: $data_installed_size"
    echo "Description: INDI shared data files built from upstream v$INDI_VERSION"
  } > "$data_root/DEBIAN/control"

  mkdir -p "$dev_root/usr"
  if [[ -d "$full_stage_dir/usr/include" ]]; then
    mkdir -p "$dev_root/usr/include"
    rsync -a "$full_stage_dir/usr/include/" "$dev_root/usr/include/"
  fi

  for rel in "usr/lib/pkgconfig" "usr/lib/cmake"; do
    local src="$full_stage_dir/$rel"
    local dst="$dev_root/$rel"
    if [[ -d "$src" ]]; then
      mkdir -p "$dst"
      rsync -a "$src/" "$dst/"
    fi
  done

  if [[ -n "$multiarch" ]]; then
    for rel in "usr/lib/$multiarch/pkgconfig" "usr/lib/$multiarch/cmake"; do
      local src="$full_stage_dir/$rel"
      local dst="$dev_root/$rel"
      if [[ -d "$src" ]]; then
        mkdir -p "$dst"
        rsync -a "$src/" "$dst/"
      fi
    done
  fi

  for librel in "${lib_roots[@]}"; do
    local src="$full_stage_dir/$librel"
    local dst="$dev_root/$librel"
    if [[ -d "$src" ]]; then
      mkdir -p "$dst"
      find "$src" -maxdepth 1 \( -type l -o -type f \) \( -name 'lib*.so' -o -name 'lib*.a' \) -exec cp -a {} "$dst/" \;
    fi
  done

  [[ -d "$dev_root/usr/include/libindi" ]] || {
    rm -rf "$full_stage_dir"
    fail "Expected development headers under $dev_root/usr/include/libindi"
  }

  local dev_installed_size
  dev_installed_size="$(du -sk "$dev_root/usr" | cut -f1)"

  {
    echo "Package: libindi-dev"
    echo "Version: $package_version"
    echo "Section: libdevel"
    echo "Priority: optional"
    echo "Architecture: $DEB_ARCH"
    echo "Maintainer: PINS Team"
    echo "Depends: libindi1 (= $package_version), libc6, libstdc++6"
    echo "Replaces: libindi-dev (<< $INDI_VERSION)"
    echo "Installed-Size: $dev_installed_size"
    echo "Description: INDI development headers and build metadata built from upstream v$INDI_VERSION"
  } > "$dev_root/DEBIAN/control"

  rm -rf "$full_stage_dir"

  mkdir -p artifacts

  local runtime_output="artifacts/indi-bin_${package_version}_${DEB_ARCH}_${RELEASE_DATE}.deb"
  local lib_output="artifacts/libindi1_${package_version}_${DEB_ARCH}_${RELEASE_DATE}.deb"
  local data_output="artifacts/libindi-data_${package_version}_${DEB_ARCH}_${RELEASE_DATE}.deb"
  local dev_output="artifacts/libindi-dev_${package_version}_${DEB_ARCH}_${RELEASE_DATE}.deb"

  dpkg-deb --root-owner-group --build "$runtime_root" "$runtime_output"
  sha256sum "$runtime_output" > "$runtime_output.sha256"
  record_built_deb "$runtime_output"

  dpkg-deb --root-owner-group --build "$lib_root" "$lib_output"
  sha256sum "$lib_output" > "$lib_output.sha256"
  record_built_deb "$lib_output"

  dpkg-deb --root-owner-group --build "$data_root" "$data_output"
  sha256sum "$data_output" > "$data_output.sha256"
  record_built_deb "$data_output"

  dpkg-deb --root-owner-group --build "$dev_root" "$dev_output"
  sha256sum "$dev_output" > "$dev_output.sha256"
  record_built_deb "$dev_output"

  log "Built INDI packages"
}

configure_phd2_service_file() {
  local service_file="$1"
  [[ -f "$service_file" ]] || return

  sed -i \
    -e "s|^User=.*|User=$TARGET_USER|" \
    -e "s|^Group=.*|Group=$TARGET_USER|" \
    -e "s|^Environment=DISPLAY=.*|Environment=DISPLAY=:0|" \
    -e "s|^Environment=XAUTHORITY=.*|Environment=XAUTHORITY=$TARGET_HOME/.Xauthority|" \
    -e "s|^Environment=HOME=.*|Environment=HOME=$TARGET_HOME|" \
    -e "s|^WorkingDirectory=.*|WorkingDirectory=$TARGET_HOME|" \
    -e "s|^Restart=.*|Restart=on-failure|" \
    -e "s|/home/pi|$TARGET_HOME|g" \
    "$service_file"

  local tmp_service
  tmp_service="$(mktemp)"
  awk -v home="$TARGET_HOME" '
    /^ConditionPathExists=\/tmp\/\.X11-unix\/X0$/ { next }
    /^ConditionPathExists=.*\.Xauthority$/ { next }
    { print }
    $0 ~ /^After=.*graphical\.target/ {
      print "ConditionPathExists=/tmp/.X11-unix/X0"
      print "ConditionPathExists=" home "/.Xauthority"
    }
  ' "$service_file" > "$tmp_service"
  mv "$tmp_service" "$service_file"
}

build_phd2_package() {
  log "Building PHD2 Debian package for $DEB_ARCH"

  ensure_modern_libxisf_if_enabled

  local phd2_src="artifacts/src/phd2"
  rm -rf "$phd2_src"
  mkdir -p "$(dirname "$phd2_src")"
  git clone --branch "$PHD2_BRANCH" --single-branch "$PHD2_REPO_URL" "$phd2_src"

  local phd2_service_candidates=(
    "$phd2_src/debian/phd2.service"
    "$phd2_src/debian/systemd/phd2.service"
  )
  local phd2_service_src
  for phd2_service_src in "${phd2_service_candidates[@]}"; do
    if [[ -f "$phd2_service_src" ]]; then
      configure_phd2_service_file "$phd2_service_src"
    fi
  done

  # The workflow enables source repositories before running mk-build-deps.
  run_as_root bash -lc "set -euo pipefail; \
    if [ -f /etc/apt/sources.list.d/ubuntu.sources ]; then \
      sed -i 's/^Types: deb$/Types: deb deb-src/' /etc/apt/sources.list.d/ubuntu.sources; \
    elif [ -f /etc/apt/sources.list.d/debian.sources ]; then \
      sed -i 's/^Types: deb$/Types: deb deb-src/' /etc/apt/sources.list.d/debian.sources; \
    elif [ -f /etc/apt/sources.list ]; then \
      sed -i '/^deb /p; s/^deb /deb-src /' /etc/apt/sources.list; \
    fi"
  run_as_root apt-get update

  local projects_home
  projects_home="${HOME}/Projects"
  mkdir -p "$projects_home" "$projects_home/build"

  local indi_core_src="$projects_home/indi-core"
  local indi_core_build="$projects_home/build/indi-core"
  rm -rf "$indi_core_src" "$indi_core_build"
  git clone --depth 1 --branch "v$PHD2_INDI_VERSION" https://github.com/indilib/indi.git "$indi_core_src"
  cmake -S "$indi_core_src" -B "$indi_core_build" \
    -DCMAKE_INSTALL_PREFIX=/usr \
    -DCMAKE_BUILD_TYPE=Release
  cmake --build "$indi_core_build" --parallel "$(nproc)"
  run_as_root cmake --install "$indi_core_build"
  run_as_root ldconfig

  local equivs_dir="/tmp/libindi-dev-equivs"
  rm -rf "$equivs_dir"
  mkdir -p "$equivs_dir"
  cat > "$equivs_dir/control" <<EOF
Section: misc
Priority: optional
Standards-Version: 4.7.0

Package: libindi-dev
Version: ${PHD2_INDI_VERSION}
Maintainer: Local Builder <builder@local>
Architecture: all
Description: Dummy libindi-dev package (local build)
 Dummy package to satisfy Build-Depends when INDI is built from source.
EOF
  (
    cd "$equivs_dir"
    equivs-build control
  )
  run_as_root dpkg -i "$equivs_dir/libindi-dev_${PHD2_INDI_VERSION}_all.deb"

  (
    cd "$phd2_src"
    run_as_root mk-build-deps --install --remove \
      --tool "apt-get -o Debug::pkgProblemResolver=yes --no-install-recommends -y" \
      debian/control
  )

  ensure_opencv_required_version

  if is_truthy "$PHD2_BUILD_INDI_3RDPARTY"; then
    local indi_3p_src="$projects_home/indi-3rdparty"
    local indi_3p_build="$projects_home/build/indi-3rdparty"
    rm -rf "$indi_3p_src" "$indi_3p_build"
    git clone --depth 1 --branch "v$PHD2_INDI_VERSION" https://github.com/indilib/indi-3rdparty.git "$indi_3p_src"

    # FindINDI.cmake honors INDI_ROOT; pass it explicitly to avoid include detection failures.
    if cmake -S "$indi_3p_src" -B "$indi_3p_build" \
      -DCMAKE_INSTALL_PREFIX=/usr \
      -DCMAKE_BUILD_TYPE=Release \
      -DINDI_ROOT="$PHD2_INDI_ROOT" \
      -DCMAKE_PREFIX_PATH="$PHD2_INDI_ROOT:${CMAKE_PREFIX_PATH:-}"; then
      if cmake --build "$indi_3p_build" --parallel "$(nproc)"; then
        if run_as_root cmake --install "$indi_3p_build"; then
          run_as_root ldconfig || true
        else
          warn "indi-3rdparty install failed; continuing without installed indi-3rdparty drivers"
        fi
      else
        warn "indi-3rdparty build failed; continuing without indi-3rdparty drivers"
      fi
    else
      warn "indi-3rdparty configure failed (INDI_ROOT=$PHD2_INDI_ROOT); continuing without indi-3rdparty drivers"
    fi
  else
    log "Skipping indi-3rdparty build (PHD2_BUILD_INDI_3RDPARTY=$PHD2_BUILD_INDI_3RDPARTY)"
  fi

  (
    cd "$phd2_src"

    local orig_version
    orig_version="$(sed -n '1s/^.*(\([^)]*\)).*/\1/p' debian/changelog)"
    [[ -n "$orig_version" ]] || fail "Unable to parse PHD2 debian/changelog version"

    local base_version
    base_version="$(printf '%s' "$orig_version" | sed -E 's/-[A-Za-z0-9.+~]+$//')"
    local new_version
    new_version="${base_version}-$((30 + BUILD_NUMBER))"

    export DEBEMAIL="builder@local"
    export DEBFULLNAME="Local Builder"
    dch --noauto-nmu -v "$new_version" "Local x64 build ${BUILD_NUMBER}"

    export PKG_CONFIG_PATH="$OPENCV_INSTALL_PREFIX/lib/pkgconfig:${PKG_CONFIG_PATH:-}"
    export LD_LIBRARY_PATH="$OPENCV_INSTALL_PREFIX/lib:${LD_LIBRARY_PATH:-}"
    export CMAKE_PREFIX_PATH="$OPENCV_INSTALL_PREFIX:${CMAKE_PREFIX_PATH:-}"
    export OpenCV_DIR="$OPENCV_INSTALL_PREFIX/lib/cmake/opencv4"
    export DEB_BUILD_OPTIONS="nocheck parallel=$(nproc)"

    dpkg-buildpackage -b -us -uc -a "$DEB_ARCH"
  )

  mkdir -p artifacts
  find artifacts/src -maxdepth 3 -type f -name '*.deb' -path '*/phd2*' -exec cp -f {} artifacts/ \;

  local phd2_deb
  phd2_deb="$(find artifacts -maxdepth 1 -type f -name 'phd2_*_*.deb' ! -name '*dbgsym*' | sort | tail -n 1 || true)"
  [[ -n "$phd2_deb" ]] || fail "Failed to locate built PHD2 .deb artifact"

  dpkg-deb -c "$phd2_deb" | tee /tmp/phd2-deb-contents.txt >/dev/null
  grep -q './etc/systemd/system/phd2.service' /tmp/phd2-deb-contents.txt || fail "phd2.service missing inside package"

  sha256sum "$phd2_deb" > "$phd2_deb.sha256"
  record_built_deb "$phd2_deb"

  log "Built PHD2 package: $phd2_deb"
}

create_firmware_bundle() {
  log "Creating firmware bundle zip"

  local release_time
  release_time="$(date +%H%M%S)"
  local bundle_path="artifacts/firmware_${RELEASE_DATE}_${release_time}.zip"

  local bundle_files=()
  local deb_path
  for deb_path in "${BUILT_DEBS[@]}"; do
    [[ -f "$deb_path" ]] && bundle_files+=("$deb_path")
    [[ -f "$deb_path.sha256" ]] && bundle_files+=("$deb_path.sha256")
  done

  [[ ${#bundle_files[@]} -gt 0 ]] || fail "No package artifacts found to include in firmware bundle"

  rm -f "$bundle_path"
  zip -j "$bundle_path" "${bundle_files[@]}"

  log "Created firmware bundle: $bundle_path"
}

install_built_packages() {
  log "Installing all built Debian packages"

  [[ ${#BUILT_DEBS[@]} -gt 0 ]] || fail "No built package paths recorded"

  mapfile -t unique_debs < <(printf '%s\n' "${BUILT_DEBS[@]}" | awk '!seen[$0]++')

  local deb
  for deb in "${unique_debs[@]}"; do
    [[ -f "$deb" ]] || fail "Missing expected .deb artifact: $deb"
  done

  run_as_root dpkg -i "${unique_debs[@]}" || true
  run_as_root apt-get -f install -y
  run_as_root dpkg -i "${unique_debs[@]}"

  local nina_data_dir="$TARGET_HOME/.local/share/NINA"
  local nina_config_dir="$TARGET_HOME/.config/NINA"
  run_as_root mkdir -p "$nina_data_dir" "$nina_config_dir"
  run_as_root chown -R "$TARGET_USER:$TARGET_USER" "$nina_data_dir" "$nina_config_dir"

  local phd2_service_path="/etc/systemd/system/phd2.service"
  if [[ -f "$phd2_service_path" ]]; then
    local tmp_phd2_service
    tmp_phd2_service="$(mktemp)"
    cp "$phd2_service_path" "$tmp_phd2_service"
    configure_phd2_service_file "$tmp_phd2_service"
    run_as_root install -m 644 "$tmp_phd2_service" "$phd2_service_path"
    rm -f "$tmp_phd2_service"
  fi

  if command -v systemctl >/dev/null 2>&1; then
    run_as_root systemctl daemon-reload || true
    if systemctl cat phd2.service >/dev/null 2>&1; then
      if is_truthy "$PHD2_AUTOSTART_SERVICE"; then
        run_as_root systemctl enable phd2.service >/dev/null 2>&1 || true
        run_as_root systemctl restart phd2.service || true
      else
        run_as_root systemctl disable --now phd2.service >/dev/null 2>&1 || true
        log "PHD2 service auto-start disabled (set PHD2_AUTOSTART_SERVICE=true to enable)"
      fi
    fi
    run_as_root systemctl restart pins.service || true
  fi

  log "Installed ${#unique_debs[@]} package(s)"
}

setup_framingassistant_cache() {
  log "Setting up Offline Sky Map Cache (FramingAssistantCache)"

  local tmp_dir
  tmp_dir="$(mktemp -d)"
  local zip_path="$tmp_dir/FramingAssistantCache_Full.zip"
  local unzip_dir="$tmp_dir/unzipped"

  mkdir -p "$FRAMINGASSISTANT_CACHE_ROOT"

  if ! curl -L --fail --retry 3 --retry-delay 5 -o "$zip_path" "$FRAMINGASSISTANT_CACHE_URL"; then
    rm -rf "$tmp_dir"
    return 1
  fi

  rm -rf "$FRAMINGASSISTANT_CACHE_DIR"
  mkdir -p "$FRAMINGASSISTANT_CACHE_DIR"
  unzip -q "$zip_path" -d "$unzip_dir"

  if [[ -d "$unzip_dir/FramingAssistantCache" ]]; then
    rsync -a "$unzip_dir/FramingAssistantCache/" "$FRAMINGASSISTANT_CACHE_DIR/"
  elif [[ -d "$unzip_dir/framingassistantcache" ]]; then
    rsync -a "$unzip_dir/framingassistantcache/" "$FRAMINGASSISTANT_CACHE_DIR/"
  else
    rsync -a "$unzip_dir/" "$FRAMINGASSISTANT_CACHE_DIR/"
  fi

  run_as_root chown -R "$TARGET_USER:$TARGET_USER" "$FRAMINGASSISTANT_CACHE_DIR" || true
  rm -rf "$tmp_dir"

  log "FramingAssistant cache installed at: $FRAMINGASSISTANT_CACHE_DIR"
}

setup_astap() {
  log "Setting up ASTAP"

  run_as_root apt-get update
  run_as_root apt-get install -y astap || true

  if command -v astap >/dev/null 2>&1; then
    log "ASTAP available at: $(command -v astap)"
    warn "ASTAP star database files are still required and must be installed separately"
    return 0
  fi

  if [[ -x "$ASTAP_PRIMARY_PATH" || -x "$ASTAP_ALT_PATH" ]]; then
    log "ASTAP CLI already present"
    return 0
  fi

  if [[ -n "$ASTAP_CLI_SOURCE" ]]; then
    if [[ ! -f "$ASTAP_CLI_SOURCE" ]]; then
      warn "ASTAP_CLI_SOURCE does not exist: $ASTAP_CLI_SOURCE"
      return 1
    fi

    run_as_root install -m 755 "$ASTAP_CLI_SOURCE" "$ASTAP_PRIMARY_PATH"
    run_as_root ln -sf "$ASTAP_PRIMARY_PATH" "$ASTAP_ALT_PATH"
    "$ASTAP_PRIMARY_PATH" -h >/dev/null 2>&1 || true

    log "ASTAP CLI installed at: $ASTAP_PRIMARY_PATH"
    warn "ASTAP star database files are still required and must be installed separately"
    return 0
  fi

  warn "ASTAP not found in apt and ASTAP_CLI_SOURCE is not set"
  warn "Set ASTAP_CLI_SOURCE to a local astap_cli binary to install manually"
  return 1
}

setup_runtime_prerequisites() {
  if ! is_truthy "$SETUP_RUNTIME_PREREQS"; then
    log "Skipping runtime prerequisites setup"
    return
  fi

  log "Applying runtime prerequisites from BUILD_PINS.md"

  local runtime_errors=0

  if is_truthy "$SETUP_FRAMINGASSISTANT_CACHE"; then
    if ! setup_framingassistant_cache; then
      warn "Failed to set up FramingAssistant cache"
      runtime_errors=$((runtime_errors + 1))
    fi
  fi

  if is_truthy "$SETUP_ASTAP"; then
    if ! setup_astap; then
      warn "Failed to set up ASTAP"
      runtime_errors=$((runtime_errors + 1))
    fi
  fi

  if [[ "$runtime_errors" -gt 0 ]] && is_truthy "$RUNTIME_SETUP_STRICT"; then
    fail "Runtime prerequisite setup failed with $runtime_errors error(s)"
  fi

  if [[ "$runtime_errors" -gt 0 ]]; then
    warn "Runtime setup completed with $runtime_errors warning(s)"
  else
    log "Runtime prerequisites setup completed"
  fi
}

print_summary() {
  log "Build and install completed"
  echo
  echo "Release: $RELEASE_VERSION"
  echo "Artifacts:"

  mapfile -t unique_debs < <(printf '%s\n' "${BUILT_DEBS[@]}" | awk '!seen[$0]++' | sort)
  local deb
  for deb in "${unique_debs[@]}"; do
    echo "  - $deb"
  done
}

main() {
  if [[ "$(uname -s)" != "Linux" ]]; then
    fail "This script must be run on Linux (Ubuntu expected)"
  fi

  if [[ "$(dpkg --print-architecture)" != "$DEB_ARCH" ]]; then
    warn "Host architecture is $(dpkg --print-architecture), but DEB_ARCH is $DEB_ARCH"
  fi

  ensure_repo_root

  compute_source_fingerprint
  load_previous_build_state
  if try_reuse_successful_build; then
    install_built_packages
    setup_runtime_prerequisites
    print_summary
    return
  fi

  install_build_prerequisites
  ensure_opencv_required_version
  ensure_modern_libxisf_if_enabled
  install_dotnet_10
  install_node_22
  print_tool_versions

  update_submodules_except_external
  clone_workflow_plugin_repositories

  build_and_install_libgpiod
  populate_external_bundle
  stage_opencvsharp_runtime_from_nuget

  determine_versions
  build_core_pins_package
  build_pinsdaemon_package
  build_wandereretasdk_package
  build_plugins
  build_indi_debian_packages
  build_phd2_package
  if is_truthy "$CREATE_FIRMWARE_BUNDLE"; then
    create_firmware_bundle
  else
    log "Skipping firmware bundle zip creation"
  fi
  install_built_packages
  setup_runtime_prerequisites
  save_successful_build_state

  print_summary
}

main "$@"
