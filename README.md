# XR Session Capture

Paquete de Unity para grabar sesiones en el **HTC VIVE Focus 3** y reproducirlas después, en el Editor o en el mismo visor. Sirve para pruebas que se pueden repetir: la reproducción toma capturas de pantalla en los mismos momentos que la grabación, las compara y genera un reporte con métricas de rendimiento. Es la misma idea que *Session Capture* del Meta XR Simulator, pero la grabación se hace en el visor real.

Incluye además un **overlay de métricas** dentro del visor (FPS, tiempos de CPU y GPU, frames perdidos, memoria, batería, temperatura y estado térmico).

**¿Primera vez?** La [guía paso a paso](Documentation~/guia-paso-a-paso.md) explica todo el proceso, desde preparar el PC y el visor hasta grabar, bajar y reproducir una sesión.

> **Estado:** versión 0.1.0. El código compila contra Unity 2021.3 e Input System 1.7 (runtime, Editor y la rama de Android), pero **todavía no se ha probado en Unity ni en un Focus 3**. Consulta [Primera prueba](#primera-prueba) más abajo.

## Qué se graba

Cada sesión es una carpeta con:

| Archivo | Contenido |
| --- | --- |
| `session.xrsc` | Por frame: pose de la cabeza; por control: pose del agarre y del puntero, gatillo, grip, joystick, botones y estado de seguimiento. También guarda marcadores (capturas y notas). |
| `shots/*.png` | Capturas de los puntos de control, en mono, con tamaño y FOV fijos para que sean comparables. |
| `metrics.csv` | Una fila por frame con todas las métricas. |
| `metrics-summary.json` | FPS promedio, percentiles p50, p95 y p99 del tiempo de frame, % de frames fuera de presupuesto, frames perdidos, CPU y GPU promedio, memoria máxima, batería y temperatura. |
| `replays/<fecha>/` | Una carpeta por cada reproducción, con sus capturas, `diff/`, `metrics.csv`, `report.json` y `report.html`. |

## Requisitos

- Unity 2021.3 o posterior, con el **Input System** (1.7 o posterior). En *Player Settings → Active Input Handling* debe estar **Input System Package** o **Both**.
- El plugin **VIVE OpenXR** para Android, con el perfil *VIVE Focus 3 Controller*. Funciona con cualquier perfil de OpenXR que use Input System.
- Para los tiempos de CPU y GPU, activa *Player Settings → Frame Timing Stats*. El tiempo del hilo principal solo aparece en builds de desarrollo.
- Que la app lea la entrada con el Input System (TrackedPoseDriver, XR Interaction Toolkit o tus propias `InputAction`). Lo que use la API antigua `UnityEngine.XR.InputDevices` no se reproduce.

## Instalación

En *Window → Package Manager → + → Add package from git URL*:

```
https://github.com/cesared19/re-Play-vibe-session-recording.git
```

O en `Packages/manifest.json`:

```json
"com.sparky.xr-session-capture": "https://github.com/cesared19/re-Play-vibe-session-recording.git"
```

## Uso

1. En la primera escena: *GameObject → XR → Session Capture*. Con *Don't Destroy On Load* sigue activo al cambiar de escena.
2. Compila para Android y abre la app en el visor.
3. Los controles usan el botón **Menú del control izquierdo** como modificador (se pueden cambiar en el inspector):

   | Acción | En el visor | En el Editor |
   | --- | --- | --- |
   | Mostrar u ocultar el overlay | Menú + **X** | F8 |
   | Empezar o detener la grabación | Menú + **Y** | F9 |
   | Tomar una captura (punto de control) | Menú + **gatillo** | F10 |

   *Record On Start* empieza a grabar al cargar la escena, y *Auto Screenshot Interval* toma capturas cada N segundos. Desde código: `SessionCaptureManager.Instance.StartRecording()`, `TakeScreenshot("menu-abierto")`, `AddNote("…")` y `StopRecording()`.
4. Para bajar las sesiones abre *Window → XR Session Capture* y usa **Pull captures from headset**, o desde la terminal:

   ```bash
   adb pull /sdcard/Android/data/<paquete.de.la.app>/files/SessionCaptures/. SessionCaptures/
   ```

   En el Editor se guardan en `<proyecto>/SessionCaptures` (fuera de `Assets`).

### Reproducir

- **En el Editor:** abre la escena donde se grabó y pulsa *Replay in Editor* en la ventana. No necesitas el visor: los dispositivos virtuales sustituyen al casco y los controles.
- **En el visor:** *Replay on headset* sube la sesión y reinicia la app con ella. También desde la terminal:

  ```bash
  adb shell am start -S -n <paquete>/com.unity3d.player.UnityPlayerActivity -e xrscReplay <carpeta-de-la-sesion>
  ```

  Agrega `-e xrscQuit 1` para que la app se cierre al terminar. En PC, los argumentos son `-xrscReplay <sesion>` y `-xrscQuit`.
- **Como prueba automatizada:** importa el ejemplo *Replay regression tests* desde el Package Manager y copia a `SessionCaptures/regression/` las sesiones que quieras vigilar. La prueba reproduce cada una y falla si alguna captura no coincide:

  ```bash
  Unity -batchmode -projectPath . -runTests -testPlatform PlayMode -testResults results.xml
  ```

Al terminar, `report.html` muestra, para cada captura, la grabación, la reproducción y las diferencias en rojo, junto con la tabla de rendimiento de la grabación frente a la reproducción.

### Cómo funciona la reproducción

`SessionPlayer` crea un casco y dos controles virtuales (`ReplayHMD` y `ReplayController`, que heredan de `XRHMD` y `XRController`), desactiva los reales y, antes de cada actualización del Input System, les envía el estado grabado. Todo lo que esté enlazado a `<XRHMD>` o a `<XRController>{LeftHand}`/`{RightHand}` recibe la entrada grabada.

Con *Timing* en **FrameLocked** (el valor por defecto), se reproduce un frame grabado por cada frame y `Time.captureDeltaTime` se fija al delta grabado. Así el tiempo del juego avanza igual que en la grabación aunque el Editor vaya más rápido o más lento. **RealTime** sigue el reloj e interpola, y sirve para ver la sesión a velocidad real.

### Para que las capturas coincidan

- Graba desde la carga de la escena (*Record On Start*) y reproduce en la misma escena.
- Deja activado *Use Fixed Random Seed* (fija `UnityEngine.Random` al grabar y al reproducir).
- La lógica debe usar `Time.deltaTime` o `Time.time`, no `Time.realtimeSinceStartup` ni `DateTime.Now`.
- Las cargas asíncronas, la red, los sistemas de partículas con semilla aleatoria y el audio pueden variar. Si una zona cambia sola, sube *Max Different Pixels Percent* o toma la captura en otro momento.
- Las capturas del visor y las del Editor pueden diferir un poco por calidad, sombras o MSAA. Compara reproducciones hechas en el mismo dispositivo con el que se grabó, o ajusta *Pixel Threshold*.

### Vincular sesiones con SparkyTC

Cada sesión se puede vincular con el test case de [SparkyTC](https://github.com/cesared19/SparkyTC) que automatiza, para publicar después sus reproducciones como test runs automáticos. El visor nunca se conecta a SparkyTC: todo esto ocurre en el PC.

1. En *Window → XR Session Capture*, abre la sección **SparkyTC**.
2. Escribe la **SparkyTC URL** (la dirección donde abres SparkyTC) y pega un **token de API del proyecto**. Lo crea un admin en SparkyTC, en *Settings → API tokens* del proyecto: *Read* basta para vincular y *Read and write* hace falta para publicar. Se guardan en EditorPrefs de este PC, nunca en el proyecto ni en el build.
3. Pulsa **Connect** para comprobarlo.
4. En cada sesión, **Link…** abre un buscador de test cases (por número o título). El vínculo se guarda en `sparkytc.json`, junto a `session.xrsc`, y viaja con la carpeta (por ejemplo, al moverla a `regression/`). **Open** abre el test case en SparkyTC y **Unlink** quita el vínculo.

> Si el backend de SparkyTC solo escucha en el servidor (`127.0.0.1:8080`), la API de integración no es accesible desde otro PC por el puerto de la app. Mientras SparkyTC no la publique, usa un túnel SSH (`ssh -L 8080:127.0.0.1:8080 usuario@servidor`) y pon `http://localhost:8080` en **API URL**.

## Métricas

| Columna en `metrics.csv` | Origen |
| --- | --- |
| `frame_ms`, `fps` | `Time.unscaledDeltaTime` |
| `cpu_ms`, `gpu_ms` | `FrameTimingManager` (requiere *Frame Timing Stats*) |
| `main_thread_ms` | Contador del profiler *Main Thread* (solo en builds de desarrollo) |
| `xr_app_gpu_ms`, `xr_compositor_gpu_ms`, `dropped_frames`, `refresh_hz` | `XRDisplaySubsystem` (lo que reporte el runtime de OpenXR) |
| `used_memory_mb`, `gc_alloc_kb` | Contadores *System Used Memory* y *GC Allocated In Frame* |
| `battery_pct` | `SystemInfo.batteryLevel` |
| `battery_temp_c`, `thermal_status` | Android: temperatura de la batería y `PowerManager.getCurrentThermalStatus()` (cada 2 s) |
| `left/right_controller_battery_pct` | `CommonUsages.batteryLevel`, si el runtime lo expone |

Las celdas vacías (o "—" en el overlay) indican que el dato no está disponible en ese dispositivo o con esa configuración. El overlay se muestra en la muñeca izquierda (o frente a ti si el control no tiene seguimiento) y se oculta automáticamente al tomar capturas.

## Limitaciones

- Al reproducir **en el visor**, el compositor sigue usando la cabeza real para la reproyección, así que la imagen se mueve de forma extraña si lo llevas puesto. Déjalo sobre la mesa o reproduce en el Editor.
- No graba hand tracking, eye tracking ni el botón de sistema del control derecho (lo reserva el sistema).
- Los botones que usas para controlar la grabación también quedan en ella; en la reproducción llegan a la app, pero el gestor los ignora.
- Las capturas son en mono, desde la posición de la cabeza; no son la imagen exacta de cada ojo.
- No graba video ni audio de la sesión.

## Formato `.xrsc`

Binario little-endian: `"XRSC"`, `int32` versión, JSON con `SessionInfo` y después registros con etiqueta (`0x01` frame, `0x02` marcador, `0xFF` fin). Si la app se cierra durante la grabación, se conserva todo lo grabado hasta ese momento y el reporte lo indica. Los detalles están en `Runtime/Capture/SessionFile.cs`.

## Primera prueba

Como aún no se ha ejecutado en Unity, conviene verificar en este orden:

1. Que el paquete se importe sin errores en un proyecto con VIVE OpenXR e Input System.
2. Que en el Editor, sin visor, F8, F9 y F10 muestren el overlay, graben y tomen capturas.
3. Que, al reproducir esa sesión en el Editor, el reporte dé *Passed*.
4. En el Focus 3: que el overlay se vea en ambos ojos (multiview), que Menú + X/Y/gatillo funcionen y que *Pull captures from headset* traiga las sesiones.
5. Reproducir en el Editor una sesión grabada en el visor.

## Licencia

Apache 2.0, ver [LICENSE](LICENSE).
