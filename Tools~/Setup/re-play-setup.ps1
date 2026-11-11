# re-Play · Asistente de instalación (Windows)
#
# Revisa lo que hace falta en el PC (Git, Unity con Android, adb, el visor conectado), agrega el paquete re-Play a un
# proyecto de Unity e instala la app en el visor y baja sus sesiones. Se abre con "re-Play Setup.cmd".
#
# Funciona con el PowerShell que trae Windows (5.1); no instala nada por su cuenta.
# -NoGui carga solo las funciones (para probarlas) sin abrir la ventana.
param([switch]$NoGui)

$script:RepoUrl = 'https://github.com/cesared19/re-Vibe-session-recording.git'
$script:PackageName = 'com.sparky.xr-session-capture'
$script:GuideUrl = 'https://github.com/cesared19/re-Vibe-session-recording/blob/main/Documentation~/guia-paso-a-paso.md'
$script:GitDownloadUrl = 'https://git-scm.com/downloads'
$script:UnityDownloadUrl = 'https://unity.com/download'
$script:CapturesFolder = 'SessionCaptures'

# ---------- Herramientas ----------

# Ejecuta un programa y devuelve su código de salida y lo que escribió. No usa "&" con 2>&1: en PowerShell 5.1 eso
# convierte en error cualquier línea que el programa escriba en stderr (adb lo hace para informar).
function Invoke-Tool {
    param([string]$Exe, [string[]]$Arguments, [int]$TimeoutSeconds = 600)
    $quoted = @()
    foreach ($a in $Arguments) {
        if ($a -match '[\s"]') { $quoted += '"' + ($a -replace '"', '\"') + '"' } else { $quoted += $a }
    }
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $Exe
    $info.Arguments = ($quoted -join ' ')
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.CreateNoWindow = $true
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $info
    try {
        [void]$process.Start()
    } catch {
        return [pscustomobject]@{ ExitCode = -1; Output = "No se pudo ejecutar $Exe : $($_.Exception.Message)" }
    }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        try { $process.Kill() } catch { }
        return [pscustomobject]@{ ExitCode = -1; Output = "$Exe no terminó en $TimeoutSeconds segundos." }
    }
    $process.WaitForExit()
    $text = ($stdout.Result + $stderr.Result).Trim()
    return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $text }
}

function Find-Git {
    $command = Get-Command git -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Source }
    foreach ($candidate in @("$env:ProgramFiles\Git\cmd\git.exe", "${env:ProgramFiles(x86)}\Git\cmd\git.exe", "$env:LOCALAPPDATA\Programs\Git\cmd\git.exe")) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { return $candidate }
    }
    return $null
}

# Carpetas donde Unity Hub instala los editores: la de siempre y la que el usuario haya elegido en el Hub.
function Get-UnityEditorRoots {
    $roots = @()
    if ($env:ProgramFiles) { $roots += (Join-Path $env:ProgramFiles 'Unity\Hub\Editor') }
    if ($env:APPDATA) {
        $secondary = Join-Path $env:APPDATA 'UnityHub\secondaryInstallPath.json'
        if (Test-Path -LiteralPath $secondary) {
            try {
                $value = (Get-Content -LiteralPath $secondary -Raw | ConvertFrom-Json)
                if ($value -is [string] -and $value.Trim()) { $roots += $value.Trim() }
            } catch { }
        }
    }
    return $roots
}

# Los editores de Unity instalados, del más nuevo al más viejo, y si cada uno tiene el módulo de Android.
function Find-UnityEditors {
    param([string[]]$Roots = (Get-UnityEditorRoots))
    $editors = @()
    foreach ($root in $Roots) {
        if (-not $root -or -not (Test-Path -LiteralPath $root)) { continue }
        foreach ($dir in (Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue)) {
            $editorDir = Join-Path $dir.FullName 'Editor'
            if (-not (Test-Path -LiteralPath (Join-Path $editorDir 'Unity.exe'))) { continue }
            $android = Join-Path $editorDir 'Data\PlaybackEngines\AndroidPlayer'
            $editors += [pscustomobject]@{
                Version    = $dir.Name
                Path       = $editorDir
                HasAndroid = (Test-Path -LiteralPath $android)
                Adb        = (Join-Path $android 'SDK\platform-tools\adb.exe')
            }
        }
    }
    return @($editors | Sort-Object -Property @{ Expression = { Get-SortableVersion $_.Version } } -Descending)
}

# "2022.3.45f1" -> algo que se ordena bien: 2022.3.45
function Get-SortableVersion {
    param([string]$Text)
    $numbers = [regex]::Matches($Text, '\d+') | ForEach-Object { [int]$_.Value }
    $parts = @(0, 0, 0)
    for ($i = 0; $i -lt 3 -and $i -lt @($numbers).Count; $i++) { $parts[$i] = @($numbers)[$i] }
    return ('{0:D6}.{1:D6}.{2:D6}' -f $parts[0], $parts[1], $parts[2])
}

function Find-Adb {
    param($Editors = (Find-UnityEditors))
    $command = Get-Command adb -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Source }
    $candidates = @()
    foreach ($sdk in @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT)) {
        if ($sdk) { $candidates += (Join-Path $sdk 'platform-tools\adb.exe') }
    }
    foreach ($editor in $Editors) { $candidates += $editor.Adb }
    if ($env:LOCALAPPDATA) { $candidates += (Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe') }
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { return $candidate }
    }
    return $null
}

# Los dispositivos que ve adb: serie, estado (device, unauthorized, offline) y modelo.
function Get-Headsets {
    param([string]$Adb)
    $devices = @()
    if (-not $Adb) { return $devices }
    $result = Invoke-Tool $Adb @('devices', '-l') 20
    foreach ($line in ($result.Output -split "`r?`n")) {
        if ($line -match '^(\S+)\s+(device|unauthorized|offline|no permissions)\b(.*)$') {
            # a second -match replaces $Matches, so keep these first
            $serial = $Matches[1]
            $deviceState = $Matches[2]
            $model = ''
            if ($Matches[3] -match 'model:(\S+)') { $model = $Matches[1] -replace '_', ' ' }
            $devices += [pscustomobject]@{ Serial = $serial; State = $deviceState; Model = $model }
        }
    }
    return $devices
}

# Las versiones publicadas (etiquetas vX.Y.Z), de la más nueva a la más vieja. Vacío si no hay Git o conexión.
function Get-ReleaseTags {
    param([string]$Git)
    if (-not $Git) { return @() }
    $result = Invoke-Tool $Git @('ls-remote', '--tags', '--refs', $script:RepoUrl) 30
    if ($result.ExitCode -ne 0) { return @() }
    $tags = @()
    foreach ($line in ($result.Output -split "`r?`n")) {
        if ($line -match 'refs/tags/(v\d+\.\d+\.\d+)\s*$') { $tags += $Matches[1] }
    }
    return @($tags | Sort-Object -Property @{ Expression = { Get-SortableVersion $_ } } -Descending)
}

# ---------- Proyecto de Unity ----------

function Test-UnityProject {
    param([string]$Project)
    return [bool]($Project -and (Test-Path -LiteralPath (Join-Path $Project 'Packages\manifest.json')) -and (Test-Path -LiteralPath (Join-Path $Project 'ProjectSettings')))
}

# Lo que el proyecto tiene para re-Play en Packages/manifest.json (la URL, con su versión), o $null.
function Get-InstalledPackage {
    param([string]$Project)
    $manifest = Join-Path $Project 'Packages\manifest.json'
    if (-not (Test-Path -LiteralPath $manifest)) { return $null }
    $text = [System.IO.File]::ReadAllText($manifest)
    $match = [regex]::Match($text, '"' + [regex]::Escape($script:PackageName) + '"\s*:\s*"([^"]*)"')
    if ($match.Success) { return $match.Groups[1].Value }
    return $null
}

# Agrega re-Play a las dependencias del proyecto, o cambia su versión. Edita el texto, sin reescribir el archivo,
# para conservar su formato. Deja una copia en manifest.json.bak. $Tag vacío = la rama principal.
function Install-Package {
    param([string]$Project, [string]$Tag)
    $manifest = Join-Path $Project 'Packages\manifest.json'
    if (-not (Test-Path -LiteralPath $manifest)) { throw "No hay Packages\manifest.json en $Project. Elige la carpeta de un proyecto de Unity." }
    $url = $script:RepoUrl
    if ($Tag) { $url = "$url#$Tag" }
    $text = [System.IO.File]::ReadAllText($manifest)
    $entry = '"' + $script:PackageName + '": "' + $url + '"'
    $existing = [regex]('"' + [regex]::Escape($script:PackageName) + '"\s*:\s*"[^"]*"')
    if ($existing.IsMatch($text)) {
        $updated = $existing.Replace($text, $entry.Replace('$', '$$'), 1)
    } else {
        $dependencies = [regex]::Match($text, '"dependencies"\s*:\s*\{')
        if (-not $dependencies.Success) { throw "Packages\manifest.json no tiene la sección ""dependencies""." }
        $newline = "`n"
        if ($text.Contains("`r`n")) { $newline = "`r`n" }
        $after = $text.Substring($dependencies.Index + $dependencies.Length)
        $comma = ','
        if ($after -match '^\s*\}') { $comma = '' }
        $updated = $text.Substring(0, $dependencies.Index + $dependencies.Length) + $newline + '    ' + $entry + $comma + $after
    }
    if ($updated -eq $text) { return $url }
    Copy-Item -LiteralPath $manifest -Destination ($manifest + '.bak') -Force
    [System.IO.File]::WriteAllText($manifest, $updated, (New-Object System.Text.UTF8Encoding $false))
    return $url
}

# El "Package Name" de Android del proyecto (Player Settings), para saber dónde guarda la app sus sesiones.
function Get-AndroidPackageId {
    param([string]$Project)
    $settings = Join-Path $Project 'ProjectSettings\ProjectSettings.asset'
    if (-not (Test-Path -LiteralPath $settings)) { return $null }
    $text = [System.IO.File]::ReadAllText($settings)
    $match = [regex]::Match($text, '(?m)^\s*applicationIdentifier:\s*\r?\n(?:[ \t]+\w+:.*\r?\n)*?[ \t]+Android:[ \t]*(\S+)')
    if ($match.Success) { return $match.Groups[1].Value }
    return $null
}

# ---------- Visor ----------

function Install-Apk {
    param([string]$Adb, [string]$Apk)
    return (Invoke-Tool $Adb @('install', '-r', $Apk) 900)
}

function Start-HeadsetApp {
    param([string]$Adb, [string]$PackageId)
    return (Invoke-Tool $Adb @('shell', 'monkey', '-p', $PackageId, '-c', 'android.intent.category.LAUNCHER', '1') 60)
}

# Copia las sesiones grabadas en el visor a <proyecto>\SessionCaptures.
function Copy-CapturesFromHeadset {
    param([string]$Adb, [string]$PackageId, [string]$Project)
    $target = Join-Path $Project $script:CapturesFolder
    if (-not (Test-Path -LiteralPath $target)) { [void](New-Item -ItemType Directory -Path $target) }
    return (Invoke-Tool $Adb @('pull', "/sdcard/Android/data/$PackageId/files/$($script:CapturesFolder)/.", $target) 900)
}

# Lo que hay y lo que falta, para mostrarlo.
function Get-Requirements {
    $git = Find-Git
    $editors = @(Find-UnityEditors)
    $adb = Find-Adb $editors
    $headsets = @(Get-Headsets $adb)
    $withAndroid = @($editors | Where-Object { $_.HasAndroid })
    $ready = @($headsets | Where-Object { $_.State -eq 'device' })

    $rows = @()
    if ($git) { $rows += New-Row 'Git' $true $git '' }
    else { $rows += New-Row 'Git' $false 'No está instalado' 'Unity lo usa para descargar re-Play. Instálalo y vuelve a revisar.' }

    if ($editors.Count -gt 0) { $rows += New-Row 'Unity' $true (($editors | ForEach-Object { $_.Version }) -join ', ') '' }
    else { $rows += New-Row 'Unity' $false 'No se encontró en Unity Hub' 'Instala Unity 2021.3 o posterior desde Unity Hub.' }

    if ($withAndroid.Count -gt 0) { $rows += New-Row 'Módulo Android de Unity' $true (($withAndroid | ForEach-Object { $_.Version }) -join ', ') '' }
    else { $rows += New-Row 'Módulo Android de Unity' $false 'Falta' 'En Unity Hub: Installs > (versión) > Add modules > Android Build Support, con OpenJDK y Android SDK & NDK Tools.' }

    if ($adb) { $rows += New-Row 'adb' $true $adb '' }
    else { $rows += New-Row 'adb' $false 'No se encontró' 'Viene con el módulo Android de Unity (Android SDK & NDK Tools).' }

    if ($ready.Count -gt 0) {
        $rows += New-Row 'Visor conectado' $true (($ready | ForEach-Object { ("$($_.Model) $($_.Serial)").Trim() }) -join ', ') ''
    } elseif (@($headsets | Where-Object { $_.State -eq 'unauthorized' }).Count -gt 0) {
        $rows += New-Row 'Visor conectado' $false 'Sin autorizar' 'Ponte el visor y acepta "Permitir la depuración USB" (marca "Permitir siempre").'
    } else {
        $rows += New-Row 'Visor conectado' $false 'No se ve ningún visor' 'Conéctalo con un cable USB-C de datos y activa la depuración USB (Ajustes > Avanzado > Desarrollador).'
    }
    return [pscustomobject]@{ Rows = $rows; Git = $git; Editors = $editors; Adb = $adb; Headsets = $headsets }
}

function New-Row {
    param([string]$Name, [bool]$Ok, [string]$Detail, [string]$Help)
    return [pscustomobject]@{ Name = $Name; Ok = $Ok; Detail = $Detail; Help = $Help }
}

if ($NoGui) { return }

# ---------- Ventana ----------

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$settingsDir = Join-Path $env:APPDATA 're-Play'
$settingsFile = Join-Path $settingsDir 'setup.json'
$state = @{ Check = $null; Project = '' }
if (Test-Path -LiteralPath $settingsFile) {
    try { $state.Project = [string](Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json).project } catch { }
}

$form = New-Object System.Windows.Forms.Form
$form.Text = 're-Play · Asistente de instalación'
$form.StartPosition = 'CenterScreen'
$form.ClientSize = New-Object System.Drawing.Size(760, 720)
$form.MinimumSize = New-Object System.Drawing.Size(700, 640)
$form.Font = New-Object System.Drawing.Font('Segoe UI', 9)

function New-Group {
    param([string]$Text, [int]$Top, [int]$Height)
    $group = New-Object System.Windows.Forms.GroupBox
    $group.Text = $Text
    $group.Location = New-Object System.Drawing.Point(12, $Top)
    $group.Size = New-Object System.Drawing.Size(736, $Height)
    $group.Anchor = 'Top,Left,Right'
    $form.Controls.Add($group)
    return $group
}

function New-Button {
    param($Parent, [string]$Text, [int]$Left, [int]$Top, [int]$Width)
    $button = New-Object System.Windows.Forms.Button
    $button.Text = $Text
    $button.Location = New-Object System.Drawing.Point($Left, $Top)
    $button.Size = New-Object System.Drawing.Size($Width, 28)
    $Parent.Controls.Add($button)
    return $button
}

function New-Label {
    param($Parent, [string]$Text, [int]$Left, [int]$Top, [int]$Width)
    $label = New-Object System.Windows.Forms.Label
    $label.Text = $Text
    $label.Location = New-Object System.Drawing.Point($Left, $Top)
    $label.Size = New-Object System.Drawing.Size($Width, 20)
    $label.AutoEllipsis = $true
    $Parent.Controls.Add($label)
    return $label
}

# 1. Requisitos
$groupCheck = New-Group '1. Lo que hace falta en este PC' 10 232
$list = New-Object System.Windows.Forms.ListView
$list.View = 'Details'
$list.FullRowSelect = $true
$list.HeaderStyle = 'Nonclickable'
$list.Location = New-Object System.Drawing.Point(12, 24)
$list.Size = New-Object System.Drawing.Size(712, 126)
$list.Anchor = 'Top,Left,Right'
[void]$list.Columns.Add('Requisito', 190)
[void]$list.Columns.Add('Estado', 70)
[void]$list.Columns.Add('Detalle', 430)
$groupCheck.Controls.Add($list)
$help = New-Label $groupCheck 'Selecciona un requisito que falte para ver cómo resolverlo.' 12 156 712
$help.Size = New-Object System.Drawing.Size(712, 34)
$help.Anchor = 'Top,Left,Right'
$buttonCheck = New-Button $groupCheck 'Revisar de nuevo' 12 194 130
$buttonGit = New-Button $groupCheck 'Descargar Git' 150 194 110
$buttonUnity = New-Button $groupCheck 'Descargar Unity Hub' 268 194 140
$buttonGuide = New-Button $groupCheck 'Abrir la guía paso a paso' 416 194 170

# 2. Proyecto
$groupProject = New-Group '2. Agregar re-Play a un proyecto de Unity' 250 150
[void](New-Label $groupProject 'Carpeta del proyecto:' 12 28 130)
$textProject = New-Object System.Windows.Forms.TextBox
$textProject.Location = New-Object System.Drawing.Point(146, 25)
$textProject.Size = New-Object System.Drawing.Size(480, 23)
$textProject.Anchor = 'Top,Left,Right'
$textProject.Text = $state.Project
$groupProject.Controls.Add($textProject)
$buttonBrowse = New-Button $groupProject 'Elegir…' 634 23 90
$buttonBrowse.Anchor = 'Top,Right'
$labelProject = New-Label $groupProject '' 12 58 712
$labelProject.Anchor = 'Top,Left,Right'
[void](New-Label $groupProject 'Versión:' 12 90 60)
$comboVersion = New-Object System.Windows.Forms.ComboBox
$comboVersion.DropDownStyle = 'DropDownList'
$comboVersion.Location = New-Object System.Drawing.Point(146, 87)
$comboVersion.Size = New-Object System.Drawing.Size(220, 23)
$groupProject.Controls.Add($comboVersion)
$buttonInstall = New-Button $groupProject 'Agregar re-Play al proyecto' 380 85 200
[void](New-Label $groupProject 'Cierra Unity antes, o deja que recargue los paquetes al volver a su ventana.' 12 120 712)

# 3. Visor
$groupHeadset = New-Group '3. Visor' 410 120
$labelHeadset = New-Label $groupHeadset '' 12 26 712
$labelHeadset.Anchor = 'Top,Left,Right'
$labelPackage = New-Label $groupHeadset '' 12 48 712
$labelPackage.Anchor = 'Top,Left,Right'
$buttonApk = New-Button $groupHeadset 'Instalar un .apk en el visor…' 12 78 200
$buttonLaunch = New-Button $groupHeadset 'Abrir la app en el visor' 220 78 170
$buttonPull = New-Button $groupHeadset 'Bajar las sesiones al proyecto' 398 78 210

# Registro
$log = New-Object System.Windows.Forms.TextBox
$log.Multiline = $true
$log.ReadOnly = $true
$log.ScrollBars = 'Vertical'
$log.Location = New-Object System.Drawing.Point(12, 540)
$log.Size = New-Object System.Drawing.Size(736, 168)
$log.Anchor = 'Top,Bottom,Left,Right'
$log.Font = New-Object System.Drawing.Font('Consolas', 9)
$form.Controls.Add($log)

function Write-Log {
    param([string]$Text)
    $log.AppendText(('[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss'), $Text) + [Environment]::NewLine)
}

# Muestra el cursor de espera mientras corre algo que tarda, y registra los errores en vez de cerrar la ventana.
function Invoke-Busy {
    param([string]$What, [scriptblock]$Action)
    $form.Cursor = [System.Windows.Forms.Cursors]::WaitCursor
    Write-Log $What
    [System.Windows.Forms.Application]::DoEvents()
    try {
        & $Action
    } catch {
        Write-Log ('Error: ' + $_.Exception.Message)
    } finally {
        $form.Cursor = [System.Windows.Forms.Cursors]::Default
    }
}

function Update-ProjectStatus {
    $project = $textProject.Text.Trim()
    $isProject = Test-UnityProject $project
    $buttonInstall.Enabled = $isProject
    if (-not $project) {
        $labelProject.Text = 'Elige la carpeta de tu proyecto de Unity (la que contiene Assets, Packages y ProjectSettings).'
    } elseif (-not $isProject) {
        $labelProject.Text = 'Esa carpeta no es un proyecto de Unity: no tiene Packages\manifest.json.'
    } else {
        $installed = Get-InstalledPackage $project
        if ($installed) { $labelProject.Text = 'El proyecto ya tiene re-Play: ' + $installed }
        else { $labelProject.Text = 'El proyecto todavía no tiene re-Play.' }
    }
    $packageId = $null
    if ($isProject) { $packageId = Get-AndroidPackageId $project }
    $state.PackageId = $packageId
    if ($packageId) { $labelPackage.Text = 'App del proyecto: ' + $packageId }
    elseif ($isProject) { $labelPackage.Text = 'El proyecto no tiene Package Name de Android (Player Settings > Other Settings).' }
    else { $labelPackage.Text = 'Elige el proyecto para abrir su app y bajar sus sesiones.' }
    Update-HeadsetButtons
}

function Update-HeadsetButtons {
    $check = $state.Check
    $ready = $false
    if ($check) { $ready = @($check.Headsets | Where-Object { $_.State -eq 'device' }).Count -gt 0 }
    $buttonApk.Enabled = $ready
    $buttonLaunch.Enabled = $ready -and [bool]$state.PackageId
    $buttonPull.Enabled = $ready -and [bool]$state.PackageId
}

function Update-Requirements {
    Invoke-Busy 'Revisando el PC y el visor…' {
        $check = Get-Requirements
        $state.Check = $check
        $list.Items.Clear()
        foreach ($row in $check.Rows) {
            $item = New-Object System.Windows.Forms.ListViewItem($row.Name)
            $status = 'Falta'
            if ($row.Ok) { $status = 'Listo' }
            [void]$item.SubItems.Add($status)
            [void]$item.SubItems.Add($row.Detail)
            $item.Tag = $row.Help
            if ($row.Ok) { $item.ForeColor = [System.Drawing.Color]::FromArgb(21, 128, 61) }
            else { $item.ForeColor = [System.Drawing.Color]::FromArgb(185, 28, 28) }
            [void]$list.Items.Add($item)
        }
        $missing = @($check.Rows | Where-Object { -not $_.Ok })
        if ($missing.Count -eq 0) { $help.Text = 'Todo listo en este PC.' }
        else { $help.Text = $missing[0].Name + ': ' + $missing[0].Help }

        $ready = @($check.Headsets | Where-Object { $_.State -eq 'device' })
        if ($ready.Count -gt 0) { $labelHeadset.Text = 'Visor: ' + (("$($ready[0].Model) $($ready[0].Serial)").Trim()) }
        else { $labelHeadset.Text = 'Visor: no conectado. Revisa el punto 1.' }

        # versiones publicadas; sin Git o sin conexión queda solo la más reciente del repositorio
        $selected = $comboVersion.SelectedItem
        $comboVersion.Items.Clear()
        foreach ($tag in (Get-ReleaseTags $check.Git)) { [void]$comboVersion.Items.Add($tag) }
        [void]$comboVersion.Items.Add('La más reciente (main)')
        if ($selected -and $comboVersion.Items.Contains($selected)) { $comboVersion.SelectedItem = $selected }
        else { $comboVersion.SelectedIndex = 0 }
        Write-Log ('Revisión terminada: ' + ($check.Rows.Count - $missing.Count) + ' de ' + $check.Rows.Count + ' listos.')
    }
    Update-ProjectStatus
}

$list.Add_SelectedIndexChanged({
    if ($list.SelectedItems.Count -gt 0) {
        $item = $list.SelectedItems[0]
        if ($item.Tag) { $help.Text = $item.Text + ': ' + $item.Tag } else { $help.Text = $item.Text + ': listo.' }
    }
})
$buttonCheck.Add_Click({ Update-Requirements })
$buttonGit.Add_Click({ Start-Process $script:GitDownloadUrl })
$buttonUnity.Add_Click({ Start-Process $script:UnityDownloadUrl })
$buttonGuide.Add_Click({ Start-Process $script:GuideUrl })

$buttonBrowse.Add_Click({
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = 'Elige la carpeta del proyecto de Unity'
    if ($textProject.Text -and (Test-Path -LiteralPath $textProject.Text)) { $dialog.SelectedPath = $textProject.Text }
    if ($dialog.ShowDialog($form) -eq 'OK') { $textProject.Text = $dialog.SelectedPath }
})
$textProject.Add_TextChanged({
    Update-ProjectStatus
    try {
        if (-not (Test-Path -LiteralPath $settingsDir)) { [void](New-Item -ItemType Directory -Path $settingsDir) }
        (@{ project = $textProject.Text.Trim() } | ConvertTo-Json) | Set-Content -LiteralPath $settingsFile -Encoding UTF8
    } catch { }
})

$buttonInstall.Add_Click({
    Invoke-Busy 'Agregando re-Play al proyecto…' {
        $tag = [string]$comboVersion.SelectedItem
        if ($tag -notmatch '^v\d') { $tag = '' }
        if (-not $state.Check.Git) { Write-Log 'Aviso: Git no está instalado; Unity no podrá descargar el paquete hasta que lo instales.' }
        $url = Install-Package $textProject.Text.Trim() $tag
        Write-Log ('Listo: Packages\manifest.json apunta a ' + $url)
        Write-Log 'Abre el proyecto en Unity (o vuelve a su ventana): descargará el paquete. Luego agrega GameObject > XR > Session Capture a tu primera escena.'
    }
    Update-ProjectStatus
})

$buttonApk.Add_Click({
    $dialog = New-Object System.Windows.Forms.OpenFileDialog
    $dialog.Filter = 'App de Android (*.apk)|*.apk'
    $dialog.Title = 'Elige el .apk que compiló Unity'
    if ($dialog.ShowDialog($form) -ne 'OK') { return }
    Invoke-Busy ('Instalando ' + [System.IO.Path]::GetFileName($dialog.FileName) + ' en el visor (puede tardar)…') {
        $result = Install-Apk $state.Check.Adb $dialog.FileName
        Write-Log $result.Output
        if ($result.ExitCode -eq 0 -and $result.Output -match 'Success') { Write-Log 'La app quedó instalada. Está en la Biblioteca del visor, en las apps de fuentes desconocidas.' }
        else { Write-Log 'No se instaló. Revisa el mensaje de arriba.' }
    }
})

$buttonLaunch.Add_Click({
    Invoke-Busy ('Abriendo ' + $state.PackageId + ' en el visor…') {
        $result = Start-HeadsetApp $state.Check.Adb $state.PackageId
        if ($result.ExitCode -eq 0 -and $result.Output -notmatch 'No activities found') { Write-Log 'La app se abrió en el visor.' }
        else { Write-Log ('No se pudo abrir: ¿está instalada? ' + $result.Output) }
    }
})

$buttonPull.Add_Click({
    Invoke-Busy 'Bajando las sesiones del visor…' {
        $project = $textProject.Text.Trim()
        $result = Copy-CapturesFromHeadset $state.Check.Adb $state.PackageId $project
        Write-Log $result.Output
        if ($result.ExitCode -eq 0) { Write-Log ('Sesiones copiadas a ' + (Join-Path $project $script:CapturesFolder) + '. Ábrelas en Unity: Window > XR Session Capture.') }
        else { Write-Log 'No se bajó nada: la app todavía no grabó sesiones en el visor, o no está instalada.' }
    }
})

$form.Add_Shown({ Update-Requirements })
try {
    [void]$form.ShowDialog()
} catch {
    [void][System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 're-Play · Asistente de instalación')
}
