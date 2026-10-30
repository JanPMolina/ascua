# Runbook: rotar la clave de firma JWT

Los JWT de acceso se firman con **ES256**, usando una clave ECDSA P-256 que entrega `ISigningKeyProvider` (`src/Ascua.Infrastructure/Auth/SigningKeys.cs`).
- **En Azure**, la clave vive en Key Vault y la app la lee con identidad administrada. Ese `KeyVaultSigningKeyProvider` está pendiente y es de Juan Pablo.
- **En local**, se usa una clave efímera que cambia con cada arranque.

## Cuándo rotar

- **Rotación programada:** por ejemplo, cada 90 días.
- **Sospecha de filtración:** rotación urgente.

## Rotación programada (sin cortar sesiones)

1. Crea una versión nueva de la clave en Key Vault.
2. Durante **al menos 10 minutos** (la vida de un token de acceso), el proveedor tiene que:
   - firmar con la clave nueva;
   - seguir aceptando la anterior en `ValidationKeys`.

   Cada token lleva el `kid` de la clave que lo firmó, así que se valida con la clave correcta.
3. Pasado ese tiempo, retira la clave anterior de `ValidationKeys` y deshabilita esa versión en Key Vault.

Los *refresh tokens* no son JWT: son valores opacos que se guardan con hash en `refresh_tokens`. Por eso la rotación no los afecta y las sesiones siguen activas.

## Rotación urgente (clave comprometida)

1. Crea la clave nueva y **no** mantengas la anterior en `ValidationKeys`. Todos los tokens de acceso vigentes dejan de servir al instante.
2. Si hay sospecha de que se usaron tokens falsificados, revoca también los *refresh tokens* con `UPDATE refresh_tokens SET revoked_at = now() WHERE revoked_at IS NULL` en Postgres. Todos tendrán que iniciar sesión de nuevo.
3. Revisa en `audit_log` las acciones administrativas hechas desde la fecha probable de la filtración.
4. Despliega o reinicia las réplicas para que tomen la clave nueva, si el proveedor no la refresca solo.

## Verificación

- Un login nuevo emite un JWT cuyo `kid` es el de la clave nueva.
- **Rotación programada:** un token emitido antes de la rotación sigue funcionando mientras no venza.
- **Rotación urgente:** un token emitido antes de la rotación recibe **401**.
