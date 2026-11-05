# Jugar en red local

Para jugar entre varios sin Azure: un equipo con Windows hace de servidor y los demás entran desde el navegador. Todos deben estar en la **misma red** (el mismo Wi-Fi o router). No funciona entre casas distintas.

## Antes de la partida (solo en el equipo servidor)

1. Instala y abre **Docker Desktop**.
2. Abre el puerto en el firewall, una sola vez, desde PowerShell **como administrador**:

   ```powershell
   New-NetFirewallRule -DisplayName 'Ascua red local' -Direction Inbound -Protocol TCP -LocalPort 8080 -Profile Private -Action Allow
   ```

3. Marca la red como privada: *Configuración → Red e Internet → propiedades de tu Wi-Fi → Red privada*. Hazlo solo en una red de confianza, como la de tu casa.

## Jugar

En la carpeta del repo:

```powershell
./jugar-en-red.ps1
```

Si Windows no deja ejecutar el script, usa `powershell -ExecutionPolicy Bypass -File jugar-en-red.ps1`.

El script muestra una dirección como `http://192.168.0.21:8080`. **Todos, también quien hace de servidor,** entran con esa dirección:

1. Cada uno crea su cuenta.
2. Uno crea una sala y comparte el código, o se usa una sala pública.
3. Cada uno elige un héroe distinto y marca «listo».
4. El anfitrión inicia la partida.

No uses el mismo navegador para dos jugadores salvo en ventanas privadas distintas. El equipo servidor no puede suspenderse durante la partida.

Al terminar: `./jugar-en-red.ps1 -Apagar`. Las cuentas se conservan para la próxima vez.

## Si algo falla

| Síntoma | Qué revisar |
|---|---|
| Solo entra el equipo servidor | Firewall y red privada (pasos 2 y 3). Que todos estén en el mismo Wi-Fi: algunas redes de invitados o de universidad aíslan a los equipos entre sí; un punto de acceso desde un celular lo evita |
| Entra, pero la partida no conecta | La dirección cambió: vuelve a correr el script y usa la que muestre |
| Dejó de responder de pronto | El equipo servidor se suspendió o cambió de red |
| El servidor no arranca | Docker Desktop abierto; `docker compose -p ascua-red logs api` |

## Seguridad

Va por HTTP sin cifrar, así que las contraseñas viajan en claro dentro de la red: **usen contraseñas inventadas para esto** y solo en redes de confianza. Es para probar con amigos, no sustituye al despliegue en Azure, que exige HTTPS. Postgres y Redis no publican ningún puerto, y los endpoints de desarrollo quedan apagados.
