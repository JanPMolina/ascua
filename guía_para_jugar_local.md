# Guía para jugar Ascua en red local

Un equipo hace de servidor (el **host**) y los demás entran desde su navegador. No hay que instalar nada, salvo en el equipo del host.

**Todos deben estar en el mismo Wi-Fi o router.** No funciona desde otra casa.

---

## Para el host

### Antes de que lleguen

1. Conecta el equipo al cargador y déjalo conectado al Wi-Fi. No cierres la tapa: si se suspende, la partida se detiene para todos.
2. Abre **Docker Desktop** y espera a que termine de arrancar.
3. En PowerShell, ve a la carpeta del proyecto:

   ```powershell
   cd "C:\Users\JanPa\Desktop\U\2026\2S\Diseño de software\proyecto 2"
   ```

4. Confirma que estás en la rama correcta:

   ```powershell
   git switch feat/red-local
   ```

5. Levanta el juego:

   ```powershell
   powershell -ExecutionPolicy Bypass -File jugar-en-red.ps1
   ```

6. El script termina mostrando una dirección como `http://192.168.0.21:8080`. **Usa esa, no esta de ejemplo**: puede cambiar de un día a otro.
7. Pruébala en tu celular, conectado al mismo Wi-Fi. Si carga, compártela con todos.

### Si juegan en otro Wi-Fi

El firewall y la red ya están listos para el Wi-Fi de tu casa. En cualquier otra red hazlo otra vez, y solo si confías en ella. En PowerShell **como administrador**:

```powershell
Set-NetConnectionProfile -InterfaceAlias 'Wi-Fi' -NetworkCategory Private
```

### Al terminar

```powershell
powershell -ExecutionPolicy Bypass -File jugar-en-red.ps1 -Apagar
```

Las cuentas y las partidas se conservan para la próxima vez, y al apagar se guarda el reporte en `partidas/resumen.md`.

### Después de jugar

Para ver qué héroes rinden mejor, con el servidor encendido:

```powershell
powershell -ExecutionPolicy Bypass -File reporte-partidas.ps1
```

Deja en `partidas/` el resumen con sugerencias de balance, los datos en CSV y un respaldo. Anoten también lo que sintieron en [notas-de-partidas.md](notas-de-partidas.md).

---

## Para los demás jugadores

1. Conéctate al **mismo Wi-Fi** que el host.
2. Abre en el navegador la dirección que te pase, con `http://` y `:8080` incluidos. Usa Chrome, Edge o Firefox, mejor en computadora.
3. Escribe **tu nombre** (de 3 a 32 letras sin tilde, números o `_`) y pulsa **Entrar**. No hace falta cuenta ni contraseña.
4. Pídele al host el **código de la sala**, escríbelo en **Unirse con código** y pulsa **Unirse**.
5. Elige un héroe. No se pueden repetir, así que pónganse de acuerdo.
6. Marca **listo** y espera a que el anfitrión inicie.

Si es tu primera vez, puedes practicar antes en el tutorial: la misma dirección con `/?tutorial=1` al final.

**Durante la partida:**
- **Si recargas la página,** vuelves solo a tu partida.
- **Si cierras la pestaña,** vuelve a la dirección y entra con **el mismo nombre**.
- **Si se te cae la conexión,** el juego intenta reconectarse solo. Tienes **60 segundos** para volver antes de contar como abandono.
- **Cada uno con su propio nombre:** cualquiera que escriba tu nombre entra como tú, y dos pestañas con el mismo nombre se expulsan entre sí.

---

## Crear la partida

Una sola persona crea la sala, normalmente el host:

1. Entra con la misma dirección que los demás y escribe tu nombre.
2. En **Crear sala**, ponle un nombre, déjala privada y pulsa **Crear sala**.
3. **Dicta el código en voz alta.** El botón de copiar no funciona sin HTTPS.
4. Cuando los cuatro aparezcan con héroe y marcados como listos, pulsa iniciar.

---

## Cómo se juega, en 30 segundos

Todos deciden a la vez y **cada ronda dura 20 segundos**. Si no envías nada, tu héroe defiende. Gana quien tenga **más oro** cuando caiga el Celador, y todos pierden si caen todos o se acaban las 60 rondas.

| Acción | Teclas |
|---|---|
| Mover | Flechas |
| Atacar | Shift + flecha |
| Habilidad | Q + flecha |
| Usar (cofres, levantar aliados) | E + flecha |
| Defender | Espacio |
| Cancelar Q o E | Esc |
| Señales a tu equipo | 1, 2, 3, 4 |

Las casillas marcadas muestran dónde golpeará un enemigo **en la ronda siguiente**: apártate o defiende. **Cómo jugar**, en la barra superior, explica a cada héroe.

---

## Si algo falla

| Qué pasa | Qué hacer |
|---|---|
| Solo el host puede entrar | Comprueba que todos estén en el mismo Wi-Fi. Las redes de invitados o de universidad suelen aislar a los equipos: comparte internet desde un celular y conéctense todos ahí, incluido el host. Como cambia la red, repite «Si juegan en otro Wi-Fi» y vuelve a correr el script |
| La página no carga en nadie | Docker Desktop debe estar abierto. Vuelve a correr el script |
| Entra pero la partida no conecta | La dirección cambió: vuelve a correr el script y usa la nueva |
| Se quedó congelado para todos | El equipo del host se suspendió o perdió el Wi-Fi. Despiértalo; si no vuelve, corre el script otra vez |
| «Ya estás en otra partida» | Termina o abandona esa partida antes de entrar a una nueva |
| Algo sigue sin funcionar | Host: `docker compose -p ascua-red logs api` muestra qué pasó |

Es una versión de prueba: si algo se ve raro o alguien gana demasiado fácil, anótenlo. Esa opinión es justo lo que falta para ajustar el juego.
