# Guía paso a paso: de cero a grabar y reproducir una sesión en el VIVE Focus 3

Esta guía cubre todo el proceso: preparar el PC y el visor, instalar el paquete en un proyecto de Unity, compilar la app al Focus 3, grabar una sesión, bajarla al PC y reproducirla (en el Editor y en el visor) hasta leer el reporte.

> **Antes de empezar:** el paquete es una herramienta para **tu propia app de Unity**; no es una app que se instale sola en el visor. Se agrega a un proyecto de Unity, se compila junto con la app y desde ahí graba y reproduce.
>
> Las versiones y los nombres de menús de Unity y de HTC cambian con el tiempo. Si algo no coincide con lo que ves, toma como referencia la documentación oficial de [VIVE OpenXR para Unity](https://developer.vive.com/resources/openxr/).

## Índice

1. [Qué necesitas](#1-qué-necesitas)
2. [Preparar el PC](#2-preparar-el-pc)
3. [Preparar el visor (modo desarrollador)](#3-preparar-el-visor-modo-desarrollador)
4. [Crear o abrir el proyecto de Unity](#4-crear-o-abrir-el-proyecto-de-unity)
5. [Instalar VIVE OpenXR](#5-instalar-vive-openxr)
6. [Configurar el proyecto para Android y el Focus 3](#6-configurar-el-proyecto-para-android-y-el-focus-3)
7. [Preparar una escena con XR Origin](#7-preparar-una-escena-con-xr-origin)
8. [Instalar XR Session Capture desde el repositorio](#8-instalar-xr-session-capture-desde-el-repositorio)
9. [Agregar Session Capture a la escena](#9-agregar-session-capture-a-la-escena)
10. [Probar en el Editor (sin visor)](#10-probar-en-el-editor-sin-visor)
11. [Compilar e instalar en el visor](#11-compilar-e-instalar-en-el-visor)
12. [Grabar una sesión en el visor](#12-grabar-una-sesión-en-el-visor)
13. [Bajar las sesiones al PC](#13-bajar-las-sesiones-al-pc)
14. [Reproducir en el Editor](#14-reproducir-en-el-editor)
15. [Reproducir en el visor](#15-reproducir-en-el-visor)
16. [Leer el reporte](#16-leer-el-reporte)
17. [Automatizar con pruebas de regresión (opcional)](#17-automatizar-con-pruebas-de-regresión-opcional)
18. [Solución de problemas](#18-solución-de-problemas)

---

## 1. Qué necesitas

| Qué | Detalle |
| --- | --- |
| Visor | HTC VIVE Focus 3, actualizado, con sus dos controles cargados. |
| Cable | USB-C de datos (no solo de carga) entre el visor y el PC. |
| PC | Windows 10/11 o macOS, con espacio para Unity (unos 15 GB con el módulo de Android). |
| Cuenta de HTC | La del visor, para activar el modo desarrollador si lo pide. |
| Unity | 2021.3 LTS o posterior (recomendado 2022.3 LTS), con el módulo **Android Build Support**. |
| Git | Instalado en el PC: el Package Manager de Unity lo usa para descargar paquetes desde GitHub. |

## 2. Preparar el PC

1. **Instala Git.** Descárgalo de <https://git-scm.com/downloads> y, en Windows, deja marcada la opción que lo agrega al `PATH`. Comprueba que funciona en una terminal:

   ```bash
   git --version
   ```

   Si Unity ya estaba abierto, ciérralo y vuelve a abrirlo después de instalar Git.

2. **Instala Unity Hub** desde <https://unity.com/download>.

3. **Instala Unity con el módulo de Android.** En Unity Hub: *Installs → Install Editor*, elige una versión LTS (2022.3 recomendado) y marca:
   - **Android Build Support**
     - **OpenJDK**
     - **Android SDK & NDK Tools**

   Si ya tienes Unity instalado, entra en *Installs*, abre el menú ⚙ de esa versión y elige *Add modules* para agregar lo anterior.

4. **adb.** Viene con el SDK de Android que instala Unity. La ventana del paquete lo busca sola. Si quieres usarlo también desde la terminal, la ruta típica es:
   - Windows: `C:\Program Files\Unity\Hub\Editor\<versión>\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe`
   - macOS: `/Applications/Unity/Hub/Editor/<versión>/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb`

   También puedes instalar las [Android SDK Platform-Tools](https://developer.android.com/tools/releases/platform-tools) por separado y agregarlas al `PATH`.

## 3. Preparar el visor (modo desarrollador)

1. Enciende el Focus 3 y conéctalo a Wi-Fi.
2. Activa el **modo desarrollador** y la **depuración USB**. En el Focus 3 está en *Ajustes → Avanzado → Desarrollador* (en algunas versiones del sistema primero hay que activar las opciones de desarrollador desde *Ajustes → Avanzado*). Activa **USB debugging**.
3. Conecta el visor al PC con el cable USB-C.
4. Ponte el visor: aparece un aviso para **permitir la depuración USB** desde ese PC. Marca *Permitir siempre desde este equipo* y acepta.
5. Comprueba la conexión desde una terminal:

   ```bash
   adb devices
   ```

   Debe aparecer una línea con un número de serie y la palabra `device`. Si dice `unauthorized`, vuelve a ponerte el visor y acepta el aviso. Si no aparece nada, prueba con otro cable o puerto.

## 4. Crear o abrir el proyecto de Unity

- **Si ya tienes la app:** ábrela en Unity Hub con la versión que instalaste y pasa al paso 5.
- **Si vas a empezar de cero:** en Unity Hub, *Projects → New project*, elige la plantilla **3D (URP)** o **3D (Core)**, ponle nombre y créalo.

## 5. Instalar VIVE OpenXR

El plugin VIVE OpenXR es el que permite que la app corra en el Focus 3.

1. En Unity: *Window → Package Manager*.
2. Pulsa **+** (arriba a la izquierda) y elige **Add package from git URL…**
3. Pega la URL del plugin que indique la [documentación de VIVE OpenXR](https://developer.vive.com/resources/openxr/). Hoy es:

   ```
   https://github.com/ViveSoftware/VIVE-OpenXR.git?path=com.htc.upm.vive.openxr
   ```

   y pulsa **Add**. Esto instala también *OpenXR Plugin* y *XR Plugin Management*.
4. Si Unity pregunta si quieres activar el nuevo **Input System** y reiniciar, acepta (**Yes**).

## 6. Configurar el proyecto para Android y el Focus 3

### 6.1 Cambiar la plataforma a Android

*File → Build Settings → Android → Switch Platform*. Tarda unos minutos la primera vez.

### 6.2 Activar OpenXR para Android

1. *Edit → Project Settings → XR Plug-in Management*.
2. En la pestaña de **Android** (el ícono del robot), marca **OpenXR** y el grupo de **VIVE XR Support** (o el nombre equivalente que muestre tu versión del plugin).
3. Entra en *XR Plug-in Management → OpenXR* (pestaña de Android):
   - En **Interaction Profiles** pulsa **+** y agrega **VIVE Focus 3 Controller Interaction**.
   - En **OpenXR Feature Groups**, activa las funciones de **VIVE XR Support** que el plugin marca como necesarias para el Focus 3.
   - Si aparece un ícono de advertencia ⚠ o de error, ábrelo y pulsa **Fix All**.

### 6.3 Player Settings

*Edit → Project Settings → Player*, pestaña de Android:

| Ajuste | Valor |
| --- | --- |
| *Company Name* / *Product Name* | Los de tu app. |
| *Other Settings → Identification → Package Name* | Por ejemplo, `com.tuempresa.tuapp`. **Anótalo**: lo necesitarás para bajar las sesiones. |
| *Other Settings → Minimum API Level* | Android 10 (API 29) o el que pida el plugin. |
| *Other Settings → Scripting Backend* | **IL2CPP** |
| *Other Settings → Target Architectures* | Solo **ARM64** |
| *Other Settings → Active Input Handling* | **Input System Package (New)** o **Both** |
| *Other Settings → Frame Timing Stats* | **Activado** (para ver los tiempos de CPU y GPU) |
| *Other Settings → Color Space* | **Linear** (recomendado) |

Para ver también el tiempo del hilo principal en el overlay, marca **Development Build** en *Build Settings*.

## 7. Preparar una escena con XR Origin

El paquete lee el visor y los controles a través del Input System, igual que el resto de la app. La escena necesita una cámara que siga la cabeza y que tenga la etiqueta `MainCamera`.

**Si tu app ya usa XR Interaction Toolkit o un XR Origin, no hace falta cambiar nada.** Si empiezas de cero:

1. *Window → Package Manager → Unity Registry*, busca **XR Interaction Toolkit** e instálalo.
2. En la misma página del paquete, abre la pestaña **Samples** e importa **Starter Assets**.
3. En la escena, borra la *Main Camera* que viene por defecto.
4. Agrega el XR Origin: *GameObject → XR → XR Origin (VR)*, o arrastra el prefab **XR Origin (XR Rig)** de *Samples → XR Interaction Toolkit → Starter Assets → Prefabs*.
5. Comprueba que la cámara dentro del XR Origin tenga la etiqueta **MainCamera** y un componente **Tracked Pose Driver (Input System)**.
6. Agrega algo que se vea, como un piso (*GameObject → 3D Object → Plane*) y unos cubos, y guarda la escena (*File → Save*).
7. En *File → Build Settings → Add Open Scenes*, agrega la escena a la lista **Scenes In Build**.

## 8. Instalar XR Session Capture desde el repositorio

1. *Window → Package Manager*, **+ → Add package from git URL…**
2. Pega:

   ```
   https://github.com/cesared19/re-Play-vibe-session-recording.git
   ```

3. Pulsa **Add**. En la lista aparece **XR Session Capture**. Si falta, instala también el **Input System** 1.7 o posterior.

Para fijar una versión (cuando haya etiquetas publicadas), agrega `#v0.1.0` al final de la URL. Para actualizar más adelante, selecciona el paquete en el Package Manager y pulsa **Update**.

> **Alternativa sin Git:** descarga el repositorio como .zip desde GitHub (*Code → Download ZIP*), descomprímelo y usa **+ → Add package from disk…**, eligiendo el archivo `package.json` de esa carpeta.

## 9. Agregar Session Capture a la escena

1. Abre la **primera escena** que carga la app.
2. *GameObject → XR → Session Capture*. Se crea un objeto **XR Session Capture** con el componente **Session Capture Manager**.
3. Revisa sus opciones en el Inspector. Los valores por defecto sirven para empezar:

| Opción | Para qué sirve |
| --- | --- |
| *Head Camera* | Vacío = usa `Camera.main`. |
| *Show Overlay* / *Overlay Anchor* | Muestra las métricas en la muñeca izquierda (*Left Wrist*) o frente a ti (*Follow Head*). |
| *Record On Start* | Graba sola al abrir la app. Es lo más fiable para que las reproducciones coincidan. |
| *Use Fixed Random Seed* | Fija `UnityEngine.Random` para que lo aleatorio se repita. Déjalo activado. |
| *Auto Screenshot Interval* | Toma una captura cada N segundos (0 = solo a mano). |
| *Screenshot Size* / *Field Of View* | Tamaño y campo de visión de las capturas (1280×720 y 90° por defecto). |
| *Replay Options → Timing* | **FrameLocked** para pruebas, **RealTime** para ver la sesión a velocidad real. |
| *Pixel Threshold* / *Max Different Pixels Percent* | Cuánto puede cambiar una captura y seguir contando como igual. |

4. Guarda la escena.

## 10. Probar en el Editor (sin visor)

Esto comprueba que todo está bien instalado antes de compilar.

1. Pulsa **Play** y haz clic dentro de la ventana *Game* para que reciba el teclado.
2. **F8** muestra u oculta el overlay. Sin visor aparece frente a la cámara, con FPS, la gráfica de tiempos y el estado *Idle*.
3. **F9** empieza a grabar: el overlay muestra **● REC** con el tiempo.
4. **F10** toma una captura (punto de control). Toma dos o tres.
5. **F9** otra vez detiene la grabación.
6. Sal de Play. La sesión quedó en `<carpeta del proyecto>/SessionCaptures/<fecha>_<escena>/`.
7. Abre *Window → XR Session Capture*: la sesión aparece en la lista.

Sin visor, la cabeza y los controles no se mueven y las capturas salen todas iguales. Sirve igual para comprobar que todo funciona; las pruebas reales se hacen grabando en el visor.

## 11. Compilar e instalar en el visor

1. Conecta el visor por USB y comprueba que `adb devices` lo muestra (paso 3).
2. *File → Build Settings*. En **Run Device** elige el Focus 3 (si no aparece, pulsa **Refresh**).
3. Pulsa **Build And Run**, elige dónde guardar el `.apk` y espera. Unity instala la app y la abre en el visor.
4. La app queda instalada en el visor, en la **Biblioteca**, dentro de las apps de **Fuentes desconocidas** (o como lo llame tu versión del sistema). Desde ahí puedes abrirla cuando quieras, sin el PC.

## 12. Grabar una sesión en el visor

Los controles usan el botón **Menú del control izquierdo** como modificador: mantenlo pulsado y presiona el otro botón.

| Acción | Botones |
| --- | --- |
| Mostrar u ocultar el overlay | **Menú + X** |
| Empezar o detener la grabación | **Menú + Y** |
| Tomar una captura (punto de control) | **Menú + gatillo izquierdo** |

1. Abre la app en el visor. Si activaste *Record On Start*, ya está grabando: el overlay de la muñeca izquierda muestra **● REC**.
2. Si no, pulsa **Menú + Y** para empezar.
3. Haz la prueba que quieras repetir después (por ejemplo, abrir un menú, tomar un objeto o recorrer un nivel).
4. En los momentos que quieras verificar, pulsa **Menú + gatillo**. Cada captura es un punto que la reproducción comparará.
5. Pulsa **Menú + Y** para terminar. El overlay vuelve a *Idle*.

Consejos:
- Empieza la prueba desde la carga de la escena (*Record On Start*) para que la reproducción arranque desde el mismo estado.
- Si te quitas el visor, la app se pausa y lo grabado se guarda en disco. Si la app se cierra a mitad de grabación, se conserva lo grabado hasta ese momento.
- Mientras grabas, mira el overlay: si la gráfica se pone amarilla o roja, ese tramo pasa del presupuesto de tiempo por frame.

## 13. Bajar las sesiones al PC

### Opción A: desde la ventana del paquete (recomendada)

1. Conecta el visor por USB.
2. En Unity: *Window → XR Session Capture*.
3. En **App package** escribe el *Package Name* del paso 6.3 (se llena solo con el del proyecto).
4. Si **adb** no se encontró solo, escribe su ruta completa (paso 2.4).
5. Pulsa **List devices** para comprobar que aparece el visor.
6. Pulsa **Pull captures from headset**. Las sesiones se copian a `<proyecto>/SessionCaptures/` y aparecen en la lista.

### Opción B: desde la terminal

```bash
adb pull /sdcard/Android/data/com.tuempresa.tuapp/files/SessionCaptures/. SessionCaptures/
```

Ejecútalo desde la carpeta del proyecto de Unity y reemplaza `com.tuempresa.tuapp` por tu *Package Name*.

### Qué hay en cada sesión

```
SessionCaptures/20261110-153012_MiEscena/
├── session.xrsc            ← poses, botones y marcadores
├── shots/                  ← capturas de los puntos de control
├── metrics.csv             ← métricas por frame (se abre en Excel)
├── metrics-summary.json    ← resumen: FPS, percentiles, memoria, temperatura…
└── replays/                ← una carpeta por cada reproducción
```

## 14. Reproducir en el Editor

No necesitas el visor conectado.

1. Abre en Unity **la misma escena** en la que grabaste.
2. *Window → XR Session Capture*. En la sesión, pulsa **Replay in Editor**.
3. Unity entra en Play y reproduce la sesión: la cámara y los controles se mueven como en la grabación, y el overlay muestra **▶ REPLAY** con el porcentaje.
4. Al terminar compara las capturas (*Comparing screenshots…*) y en la consola aparece `Replay PASSED` o `Replay FAILED`, con la ruta del reporte.
5. En la ventana, la sesión muestra **✓ last replay passed** o **✗ last replay failed**. Despliégala y pulsa **Report** para abrir el reporte.

Durante la reproducción, **F9** la detiene. El reporte se genera igual, con lo que alcanzó a reproducirse.

## 15. Reproducir en el visor

Sirve para medir el rendimiento en el hardware real con exactamente las mismas acciones.

1. Conecta el visor por USB.
2. En *Window → XR Session Capture*, pulsa **Replay on headset** en la sesión. La ventana sube la sesión al visor y reinicia la app con ella.
3. Deja el visor quieto sobre la mesa mientras reproduce. Si lo llevas puesto, la imagen se mueve de forma extraña, porque el sistema sigue usando tu cabeza real.
4. Cuando termine (el overlay vuelve a *Idle*), pulsa **Pull captures from headset**. El reporte queda en `replays/` y aparece en la ventana.

Desde la terminal es lo mismo. El nombre de la sesión es el de su carpeta:

```bash
adb push SessionCaptures/20261110-153012_MiEscena /sdcard/Android/data/com.tuempresa.tuapp/files/SessionCaptures/
adb shell am start -S -n com.tuempresa.tuapp/com.unity3d.player.UnityPlayerActivity -e xrscReplay 20261110-153012_MiEscena -e xrscQuit 1
```

`-e xrscQuit 1` cierra la app al terminar. Si tu proyecto usa otra actividad de inicio (por ejemplo, GameActivity en Unity 2023 o posterior), cámbiala en `-n`. La ventana del paquete la detecta sola.

## 16. Leer el reporte

`report.html` se abre en el navegador y tiene tres partes:

1. **Resultado:** *Passed* si todas las capturas coinciden; *Failed* si alguna cambió. Debajo, la lista de hallazgos (capturas distintas, capturas faltantes, sesión cortada o regresión de rendimiento).
2. **Rendimiento:** grabación frente a reproducción: FPS promedio, tiempo de frame p50, p95 y p99, porcentaje de frames fuera de presupuesto, frames perdidos, CPU, GPU, memoria máxima y temperatura máxima. Solo es comparable si las dos se hicieron en el visor.
3. **Capturas:** para cada punto de control, la imagen de la grabación, la de la reproducción y las diferencias en rojo.

Si una captura falla por algo que cambia solo (partículas, un reloj en pantalla, una animación que no depende de `Time.deltaTime`), sube *Max Different Pixels Percent* o *Pixel Threshold*, o toma la captura en otro momento.

## 17. Automatizar con pruebas de regresión (opcional)

1. *Window → Package Manager → XR Session Capture → Samples*, e importa **Replay regression tests**.
2. Crea la carpeta `SessionCaptures/regression/` en el proyecto y copia ahí las sesiones que quieras vigilar.
3. *Window → General → Test Runner → PlayMode → Run All*. Cada sesión es una prueba que falla si alguna captura cambió.
4. Para correrlas sin abrir Unity (por ejemplo, en un servidor), cierra el proyecto y ejecuta:

   ```bash
   Unity -batchmode -projectPath . -runTests -testPlatform PlayMode -testResults results.xml
   ```

   En Windows, `Unity` es la ruta a `Unity.exe` (por ejemplo, `"C:\Program Files\Unity\Hub\Editor\<versión>\Editor\Unity.exe"`).

## 18. Solución de problemas

| Problema | Qué revisar |
| --- | --- |
| *Add package from git URL* da error de Git | Git no está instalado o no está en el `PATH`. Instálalo y reinicia Unity Hub y Unity. |
| Errores de compilación sobre `UnityEngine.InputSystem` | Instala el Input System 1.7 o posterior y pon *Active Input Handling* en *Input System Package* o *Both*. |
| `adb devices` no muestra el visor o dice `unauthorized` | Usa un cable de datos, activa la depuración USB y acepta el aviso dentro del visor. |
| El visor no aparece en *Run Device* | Pulsa *Refresh*; comprueba `adb devices`; cierra otras herramientas que usen adb (VIVE Hub, Android Studio). |
| La app abre en 2D o no detecta el visor | Revisa el paso 6.2: OpenXR y VIVE XR Support marcados en la pestaña de **Android**, y *Fix All* aplicado. |
| Menú + X / Y / gatillo no hacen nada | Comprueba que el perfil *VIVE Focus 3 Controller Interaction* esté agregado. Puedes cambiar los botones en el Inspector del *Session Capture Manager* (*Toggle Overlay Action*, *Toggle Recording Action*, *Screenshot Action*). |
| El overlay no aparece o se ve en un solo ojo | Comprueba que *Show Overlay* esté activo y pulsa Menú + X. Si se ve en un solo ojo, prueba otro *Stereo Rendering Mode* en la configuración de OpenXR y avísanos. |
| CPU o GPU salen como "—" | Activa *Frame Timing Stats* (paso 6.3). El hilo principal solo aparece en *Development Build*. |
| *Pull captures from headset* no trae nada | El *App package* debe coincidir exactamente con el *Package Name*, y la app debe haber grabado al menos una sesión. |
| La reproducción no mueve la cámara | La cámara debe usar *Tracked Pose Driver (Input System)* (o leer el visor con Input System). Lo que use la API antigua `UnityEngine.XR.InputDevices` no se reproduce. |
| Todas las capturas fallan en el Editor con una sesión grabada en el visor | Calidad, sombras y antialiasing difieren entre el visor y el PC. Compara en el mismo dispositivo o sube *Pixel Threshold*. |
| El reporte dice "the capture was cut short" | La app se cerró antes de detener la grabación. Se reproduce hasta donde se grabó. |
