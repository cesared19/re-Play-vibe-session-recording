#!/bin/bash
# re-Play · Asistente de instalación (macOS)
#
# Revisa lo que hace falta en el Mac (Git, Unity con Android, adb, el visor conectado), agrega el paquete re-Play a un
# proyecto de Unity e instala la app en el visor y baja sus sesiones. Se abre con doble clic.
#
# Usa solo lo que trae macOS (bash, perl y los diálogos de osascript); no instala nada por su cuenta.
# RP_SOURCE_ONLY=1 carga solo las funciones (para probarlas) sin abrir las ventanas.

REPO_URL='https://github.com/cesared19/re-Vibe-session-recording.git'
PACKAGE_NAME='com.sparky.xr-session-capture'
GUIDE_URL='https://github.com/cesared19/re-Vibe-session-recording/blob/main/Documentation~/guia-paso-a-paso.md'
GIT_DOWNLOAD_URL='https://git-scm.com/downloads'
UNITY_DOWNLOAD_URL='https://unity.com/download'
CAPTURES_FOLDER='SessionCaptures'
TITLE='re-Play · Asistente de instalación'
UNITY_ROOT="${RP_UNITY_ROOT:-/Applications/Unity/Hub/Editor}"
SETTINGS_DIR="$HOME/Library/Application Support/re-Play"

# ---------- Herramientas ----------

find_git() {
	# /usr/bin/git en un Mac sin las herramientas de Xcode solo abre el instalador: cuenta si de verdad responde
	local git
	git="$(command -v git 2>/dev/null)" || return 1
	"$git" --version >/dev/null 2>&1 || return 1
	echo "$git"
}

# "2022.3.45f1" -> "002022.000003.000045", para ordenar
sortable_version() {
	echo "$1" | perl -ne 'my @n = /(\d+)/g; printf "%06d.%06d.%06d\n", $n[0] // 0, $n[1] // 0, $n[2] // 0'
}

# Los editores de Unity instalados, del más nuevo al más viejo: una línea "versión<TAB>android(1|0)<TAB>adb" por cada uno.
find_unity_editors() {
	local root="${1:-$UNITY_ROOT}" dir version android adb
	[ -d "$root" ] || return 0
	for dir in "$root"/*/; do
		dir="${dir%/}"
		[ -d "$dir/Unity.app" ] || continue
		version="$(basename "$dir")"
		android=0
		[ -d "$dir/PlaybackEngines/AndroidPlayer" ] && android=1
		adb="$dir/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb"
		printf '%s\t%s\t%s\t%s\n' "$(sortable_version "$version")" "$version" "$android" "$adb"
	done | sort -r | cut -f2-
}

find_adb() {
	local editors="$1" candidate
	if command -v adb >/dev/null 2>&1; then
		command -v adb
		return 0
	fi
	{
		[ -n "$ANDROID_HOME" ] && echo "$ANDROID_HOME/platform-tools/adb"
		[ -n "$ANDROID_SDK_ROOT" ] && echo "$ANDROID_SDK_ROOT/platform-tools/adb"
		echo "$editors" | cut -f3
		echo "$HOME/Library/Android/sdk/platform-tools/adb"
	} | while IFS= read -r candidate; do
		if [ -n "$candidate" ] && [ -x "$candidate" ]; then
			echo "$candidate"
			break
		fi
	done
}

# Los dispositivos que ve adb: una línea "serie<TAB>estado<TAB>modelo" por cada uno.
headsets() {
	local adb="$1"
	[ -n "$adb" ] || return 0
	"$adb" devices -l 2>/dev/null | perl -ne '
		if (/^(\S+)\s+(device|unauthorized|offline|no permissions)\b(.*)$/) {
			my ($serial, $state, $rest) = ($1, $2, $3);
			my $model = $rest =~ /model:(\S+)/ ? $1 : "";
			$model =~ tr/_/ /;
			print "$serial\t$state\t$model\n";
		}'
}

# Las versiones publicadas (etiquetas vX.Y.Z), de la más nueva a la más vieja. Vacío si no hay Git o conexión.
release_tags() {
	local git="$1" tag
	[ -n "$git" ] || return 0
	"$git" ls-remote --tags --refs "$REPO_URL" 2>/dev/null | perl -ne 'print "$1\n" if m{refs/tags/(v\d+\.\d+\.\d+)\s*$}' |
		while IFS= read -r tag; do printf '%s\t%s\n' "$(sortable_version "$tag")" "$tag"; done | sort -r | cut -f2
}

# ---------- Proyecto de Unity ----------

is_unity_project() {
	[ -n "$1" ] && [ -f "$1/Packages/manifest.json" ] && [ -d "$1/ProjectSettings" ]
}

# Lo que el proyecto tiene para re-Play en Packages/manifest.json (la URL, con su versión), o nada.
installed_package() {
	[ -f "$1/Packages/manifest.json" ] || return 0
	RP_PKG="$PACKAGE_NAME" perl -0777 -ne 'print $1 if /"\Q$ENV{RP_PKG}\E"\s*:\s*"([^"]*)"/' "$1/Packages/manifest.json"
}

# Agrega re-Play a las dependencias del proyecto, o cambia su versión. Edita el texto, sin reescribir el archivo, para
# conservar su formato. Deja una copia en manifest.json.bak. Sin etiqueta = la rama principal. Escribe la URL que quedó.
install_package() {
	local project="$1" tag="$2" manifest url
	manifest="$project/Packages/manifest.json"
	if [ ! -f "$manifest" ]; then
		echo "No hay Packages/manifest.json en $project. Elige la carpeta de un proyecto de Unity." >&2
		return 1
	fi
	url="$REPO_URL"
	[ -n "$tag" ] && url="$url#$tag"
	if ! grep -q '"dependencies"[[:space:]]*:[[:space:]]*{' "$manifest"; then
		echo 'Packages/manifest.json no tiene la sección "dependencies".' >&2
		return 1
	fi
	RP_PKG="$PACKAGE_NAME" RP_URL="$url" perl -0777 -i.bak -pe '
		my $entry = "\"$ENV{RP_PKG}\": \"$ENV{RP_URL}\"";
		unless (s/"\Q$ENV{RP_PKG}\E"\s*:\s*"[^"]*"/$entry/) {
			my $nl = /\r\n/ ? "\r\n" : "\n";
			# sin coma si las dependencias estaban vacías
			s/("dependencies"\s*:\s*\{)(\s*\})/$1$nl    $entry$2/ or s/("dependencies"\s*:\s*\{)/$1$nl    $entry,/;
		}' "$manifest" || return 1
	echo "$url"
}

# El "Package Name" de Android del proyecto (Player Settings), para saber dónde guarda la app sus sesiones.
android_package_id() {
	[ -f "$1/ProjectSettings/ProjectSettings.asset" ] || return 0
	perl -0777 -ne 'print $1 if /^\s*applicationIdentifier:\s*\r?\n(?:[ \t]+\w+:.*\r?\n)*?[ \t]+Android:[ \t]*(\S+)/m' "$1/ProjectSettings/ProjectSettings.asset"
}

# ---------- Visor ----------

install_apk() { "$1" install -r "$2" 2>&1; }

start_headset_app() { "$1" shell monkey -p "$2" -c android.intent.category.LAUNCHER 1 2>&1; }

# Copia las sesiones grabadas en el visor a <proyecto>/SessionCaptures.
pull_captures() {
	local adb="$1" package_id="$2" project="$3"
	mkdir -p "$project/$CAPTURES_FOLDER"
	"$adb" pull "/sdcard/Android/data/$package_id/files/$CAPTURES_FOLDER/." "$project/$CAPTURES_FOLDER" 2>&1
}

# Revisa todo y deja el resultado en variables: GIT, EDITORS, ADB, HEADSET (serie del visor listo), STATUS (el texto)
# y MISSING (cuántos requisitos faltan).
check_requirements() {
	local versions android_versions devices unauthorized model
	GIT="$(find_git)"
	EDITORS="$(find_unity_editors)"
	ADB="$(find_adb "$EDITORS")"
	devices="$(headsets "$ADB")"
	HEADSET="$(echo "$devices" | awk -F'\t' '$2 == "device" { print $1; exit }')"
	model="$(echo "$devices" | awk -F'\t' '$2 == "device" { print $3; exit }')"
	unauthorized="$(echo "$devices" | awk -F'\t' '$2 == "unauthorized" { print $1; exit }')"
	versions="$(echo "$EDITORS" | cut -f1 | paste -sd, - | sed 's/,/, /g')"
	android_versions="$(echo "$EDITORS" | awk -F'\t' '$2 == 1 { print $1 }' | paste -sd, - | sed 's/,/, /g')"
	MISSING=0
	STATUS=''
	add() { STATUS="$STATUS$1
"; }
	missing() { MISSING=$((MISSING + 1)); add "✗ $1"; add "     $2"; }

	if [ -n "$GIT" ]; then add "✓ Git"; else missing 'Git: no está instalado' 'Unity lo usa para descargar re-Play. Instálalo (o las herramientas de Xcode) y vuelve a revisar.'; fi
	if [ -n "$versions" ]; then add "✓ Unity: $versions"; else missing 'Unity: no se encontró en Unity Hub' 'Instala Unity 2021.3 o posterior desde Unity Hub.'; fi
	if [ -n "$android_versions" ]; then add "✓ Módulo Android de Unity: $android_versions"; else missing 'Módulo Android de Unity: falta' 'En Unity Hub: Installs > (versión) > Add modules > Android Build Support, con OpenJDK y Android SDK & NDK Tools.'; fi
	if [ -n "$ADB" ]; then add "✓ adb"; else missing 'adb: no se encontró' 'Viene con el módulo Android de Unity (Android SDK & NDK Tools).'; fi
	if [ -n "$HEADSET" ]; then
		add "✓ Visor conectado: $(echo "$model $HEADSET" | sed 's/^ //')"
	elif [ -n "$unauthorized" ]; then
		missing 'Visor: sin autorizar' 'Ponte el visor y acepta "Permitir la depuración USB" (marca "Permitir siempre").'
	else
		missing 'Visor: no se ve ninguno' 'Conéctalo con un cable USB-C de datos y activa la depuración USB (Ajustes > Avanzado > Desarrollador).'
	fi
}

# El estado del proyecto elegido, en variables: PROJECT_OK, PACKAGE_ID y PROJECT_STATUS (el texto).
check_project() {
	local installed
	PROJECT_OK=0
	PACKAGE_ID=''
	if [ -z "$PROJECT" ]; then
		PROJECT_STATUS='Proyecto de Unity: sin elegir.'
	elif ! is_unity_project "$PROJECT"; then
		PROJECT_STATUS="Proyecto: $PROJECT
     No es un proyecto de Unity (no tiene Packages/manifest.json)."
	else
		PROJECT_OK=1
		installed="$(installed_package "$PROJECT")"
		PACKAGE_ID="$(android_package_id "$PROJECT")"
		PROJECT_STATUS="Proyecto: $PROJECT"
		if [ -n "$installed" ]; then
			PROJECT_STATUS="$PROJECT_STATUS
     Ya tiene re-Play: $installed"
		else
			PROJECT_STATUS="$PROJECT_STATUS
     Todavía no tiene re-Play."
		fi
		if [ -n "$PACKAGE_ID" ]; then
			PROJECT_STATUS="$PROJECT_STATUS
     App: $PACKAGE_ID"
		else
			PROJECT_STATUS="$PROJECT_STATUS
     Sin Package Name de Android (Player Settings > Other Settings)."
		fi
	fi
}

[ -n "$RP_SOURCE_ONLY" ] && return 0 2>/dev/null

# ---------- Ventanas (osascript) ----------

# Los textos van como argumentos, nunca dentro del código de AppleScript.
ui_message() {
	osascript - "$TITLE" "$1" >/dev/null 2>&1 <<'APPLESCRIPT'
on run argv
	display dialog (item 2 of argv) with title (item 1 of argv) buttons {"Aceptar"} default button 1
end run
APPLESCRIPT
}

# Muestra el estado y las acciones; escribe la acción elegida, o nada si se cierra.
ui_menu() {
	local prompt="$1"
	shift
	osascript - "$TITLE" "$prompt" "$@" 2>/dev/null <<'APPLESCRIPT'
on run argv
	set choice to choose from list (items 3 thru -1 of argv) with title (item 1 of argv) with prompt (item 2 of argv) OK button name "Continuar" cancel button name "Salir"
	if choice is false then return ""
	return item 1 of choice
end run
APPLESCRIPT
}

ui_choose_folder() {
	osascript -e 'POSIX path of (choose folder with prompt "Elige la carpeta del proyecto de Unity (la que contiene Assets, Packages y ProjectSettings)")' 2>/dev/null
}

ui_choose_apk() {
	osascript -e 'POSIX path of (choose file with prompt "Elige el .apk que compiló Unity" of type {"apk"})' 2>/dev/null
}

mkdir -p "$SETTINGS_DIR"
PROJECT="$(cat "$SETTINGS_DIR/project" 2>/dev/null)"

A_CHECK='Revisar de nuevo'
A_PROJECT='Elegir el proyecto de Unity…'
A_INSTALL='Agregar re-Play al proyecto'
A_APK='Instalar un .apk en el visor…'
A_LAUNCH='Abrir la app en el visor'
A_PULL='Bajar las sesiones al proyecto'
A_GUIDE='Abrir la guía paso a paso'
A_GIT='Descargar Git'
A_UNITY='Descargar Unity Hub'

echo "$TITLE"
echo 'Esta ventana de Terminal muestra lo que va haciendo el asistente. Las opciones aparecen en una ventana aparte.'
while true; do
	echo 'Revisando el Mac y el visor…'
	check_requirements
	check_project

	actions=("$A_CHECK" "$A_PROJECT")
	[ "$PROJECT_OK" = 1 ] && actions+=("$A_INSTALL")
	[ -n "$HEADSET" ] && actions+=("$A_APK")
	if [ -n "$HEADSET" ] && [ -n "$PACKAGE_ID" ]; then actions+=("$A_LAUNCH" "$A_PULL"); fi
	[ -z "$GIT" ] && actions+=("$A_GIT")
	[ -z "$EDITORS" ] && actions+=("$A_UNITY")
	actions+=("$A_GUIDE")

	if [ "$MISSING" = 0 ]; then summary='Todo listo en este Mac.'; else summary="Falta resolver $MISSING punto(s)."; fi
	choice="$(ui_menu "$STATUS
$PROJECT_STATUS

$summary Elige qué hacer:" "${actions[@]}")"

	case "$choice" in
	'') break ;;
	"$A_CHECK") ;;
	"$A_PROJECT")
		chosen="$(ui_choose_folder)"
		if [ -n "$chosen" ]; then
			PROJECT="${chosen%/}"
			echo "$PROJECT" >"$SETTINGS_DIR/project"
		fi
		;;
	"$A_INSTALL")
		tags="$(release_tags "$GIT")"
		tag=''
		if [ -n "$tags" ]; then
			versions=()
			while IFS= read -r t; do versions+=("$t"); done <<<"$tags"
			versions+=('La más reciente (main)')
			tag="$(ui_menu 'Elige la versión de re-Play:' "${versions[@]}")"
			[ -z "$tag" ] && continue
			case "$tag" in v[0-9]*) ;; *) tag='' ;; esac
		fi
		if url="$(install_package "$PROJECT" "$tag" 2>&1)"; then
			echo "Packages/manifest.json apunta a $url"
			warning=''
			[ -z "$GIT" ] && warning='

Aviso: Git no está instalado; Unity no podrá descargar el paquete hasta que lo instales.'
			ui_message "re-Play quedó agregado al proyecto:
$url

Abre el proyecto en Unity (o vuelve a su ventana): descargará el paquete. Luego agrega GameObject > XR > Session Capture a tu primera escena.$warning"
		else
			ui_message "No se pudo agregar: $url"
		fi
		;;
	"$A_APK")
		apk="$(ui_choose_apk)"
		[ -z "$apk" ] && continue
		echo "Instalando $(basename "$apk") en el visor (puede tardar)…"
		output="$(install_apk "$ADB" "$apk")"
		echo "$output"
		case "$output" in
		*Success*) ui_message 'La app quedó instalada. Está en la Biblioteca del visor, en las apps de fuentes desconocidas.' ;;
		*) ui_message "No se instaló:
$(echo "$output" | tail -n 5)" ;;
		esac
		;;
	"$A_LAUNCH")
		output="$(start_headset_app "$ADB" "$PACKAGE_ID")"
		case "$output" in
		*'No activities found'* | *rror*) ui_message "No se pudo abrir $PACKAGE_ID: ¿está instalada?
$(echo "$output" | tail -n 3)" ;;
		*) ui_message 'La app se abrió en el visor.' ;;
		esac
		;;
	"$A_PULL")
		echo 'Bajando las sesiones del visor…'
		if output="$(pull_captures "$ADB" "$PACKAGE_ID" "$PROJECT")"; then
			echo "$output"
			ui_message "Sesiones copiadas a $PROJECT/$CAPTURES_FOLDER.
Ábrelas en Unity: Window > XR Session Capture."
		else
			echo "$output"
			ui_message 'No se bajó nada: la app todavía no grabó sesiones en el visor, o no está instalada.'
		fi
		;;
	"$A_GUIDE") open "$GUIDE_URL" ;;
	"$A_GIT") open "$GIT_DOWNLOAD_URL" ;;
	"$A_UNITY") open "$UNITY_DOWNLOAD_URL" ;;
	esac
done
echo 'Listo. Puedes cerrar esta ventana.'
