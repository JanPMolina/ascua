# Librerías de terceros (vendorizadas)

Se sirven desde el mismo origen para que la CSP pueda limitarse a `script-src 'self'` y el
cliente funcione sin acceso a un CDN. Cada `<script>` de `index.html` lleva además su hash SRI.

| Archivo | Paquete npm | Origen |
|---|---|---|
| `signalr-10.0.11/signalr.min.js` | `@microsoft/signalr@10.0.11` | `dist/browser/signalr.min.js` |
| `signalr-10.0.11/signalr-protocol-msgpack.min.js` | `@microsoft/signalr-protocol-msgpack@10.0.11` (incluye `@msgpack/msgpack`) | `dist/browser/signalr-protocol-msgpack.min.js` |

Licencia MIT (.NET Foundation); avisos de terceros en `signalr-10.0.11/third-party-notices.txt`.

Para actualizar:

```bash
npm pack @microsoft/signalr@<versión> @microsoft/signalr-protocol-msgpack@<versión>
openssl dgst -sha384 -binary signalr.min.js | openssl base64 -A   # nuevo valor de integrity
```

Los archivos no se modifican (ni finales de línea): `.gitattributes` los marca como binarios.
