# Códigos do firmware (Acer Nitro ANV15-51)

Descobertos por leitura (apenas métodos `Get*`) na classe WMI `root\WMI` → `AcerGamingFunction`, neste computador.
Evidência bruta: `docs/probe-idle.txt`, `docs/probe-load.txt`, `docs/probe-safe-write.txt`.
Gerada por `bin\ForjaProbe.exe` (exige administrador; sem elevação a classe não tem instâncias).

Todas as chamadas usam `gmInput`/`gmOutput`. Entrada dos `Get*` é `UInt32`; entrada dos `Set*` é `UInt64`
(o tipo CLR precisa ser exato). `Set*` responde `gmOutput = 0` em caso de sucesso.

## Confirmado

### Sensores: `GetGamingSysInfo`

- Consulta: `0x01 | (idSensor << 8)`. **O candidato `0x02 | (id << 8)` falhou**: todas as respostas vieram `0x0000010000000000`, sem relação com o sensor.
- Resposta: status = byte 0 (0 = ok; `0x2` = pedido inválido), valor = bits 8–23.
- IDs: CPU temp `0x01`, CPU fan `0x02`, GPU fan `0x06`, GPU temp `0x0A` (conforme candidatos). Os ids `0x03` (0x36 = 54) também respondem (sensor extra, ainda sem nome); os demais devolvem 0.

Evidência (ocioso -> sob carga de CPU, 25 s):

| Sensor | Consulta | Ocioso (bruto) | Carga (bruto) | Valor |
|---|---|---|---|---|
| CPU temp | `0x101` | `0x4000` | `0x4300` | 64 -> 67 °C |
| CPU fan | `0x201` | `0xB9100` | `0xD2900` | 2961 -> 3369 RPM |
| GPU fan | `0x601` | `0xA2500` | `0xB3700` | 2597 -> 2871 RPM |
| GPU temp | `0xA01` | `0x3100` | `0x3200` | 49 -> 50 °C |

Temperatura de CPU e rotação dos dois ventiladores são legíveis e variam de forma plausível com a carga.
(A primeira rodada de leitura, antes de mudar o probe, deu `0x3E00` = 62 °C e `0xCAA00` = 3242 RPM de CPU: valores coerentes entre si.)

### Perfil de desempenho: `Get/SetGamingMiscSetting`

- Seletor `0x0B`. Leitura: `GetGamingMiscSetting(0x0B)` -> `0x100` (byte 1 = perfil `0x01` = equilibrado; status no byte 0).
- Escrita: `SetGamingMiscSetting(0x0B | (perfil << 8))`. Equilibrado = `0x10B`.
- Perfis (candidatos, só o equilibrado foi exercitado): silencioso `0x00`, equilibrado `0x01`, desempenho `0x04`.
- Escrita testada: `SetGamingMiscSetting(0x10B)` devolveu `gmOutput=0` e a releitura continuou `0x100`.
  **Limite da evidência:** o equilibrado já era o perfil ativo, então a leitura não prova que a escrita mudaria o estado. A troca para silencioso/desempenho ainda não foi testada.

### Comportamento dos ventiladores: `Get/SetGamingFanBehavior`

- Escrita: máscara CPU `0x1`, GPU `0x8` (bits 0–15); modo CPU `<< 16`, GPU `<< 22`. Auto/Auto = `0x410009`.
- Leitura: `GetGamingFanBehavior(0x9)` -> `0x4100`; entrada `0x1` -> `0x100` (CPU), entrada `0x8` -> `0x4000` (GPU).
  Ou seja, a leitura devolve o modo da CPU em `<< 8` e o da GPU em `<< 14` (8 bits abaixo da escrita). Modo 1 = Auto.
- Escrita testada: `SetGamingFanBehavior(0x410009)` devolveu `gmOutput=0`; releitura `0x4100`, igual ao estado anterior (já era Auto/Auto).
  Mesmo limite: o estado não mudou, então só se prova que o firmware aceita o código.
- Modos: Auto = 1, Max = 2, Custom = 3 (candidato, não testado).

## Não confirmado (candidatos, sem escrita testada)

- Velocidade do ventilador: `idVentilador | (pct << 8)`, CPU `1`, GPU `4`. `GetGamingFanSpeed(1)` e `(4)` devolveram `0` (modo automático). O efeito das escritas é conferido pela validação no hardware (`SenseMonitor.exe --validar-ventoinha`); até ela passar o app não usa este código.
- `SetGamingFanTable`: codificação desconhecida; `GetGamingFanTable()` devolveu `0x1`. Nenhuma escrita tentada; o app não usa a tabela.
- Perfis turbo `0x05` e econômico `0x06`: não expostos, não testados.

## Candidatos que falharam

- Consulta de sensor `0x02 | (id << 8)`: respostas constantes `0x0000010000000000`; comandos `0x03`, `0x04`, `0x05` respondem `0x2` (inválido). O comando correto é `0x01`.

## Outras leituras (sem significado conhecido)

`GetGamingMiscSetting` 0x00..0x10, `GetGamingProfileSetting` 0x0..0xF, `GetGamingProfile` 0x0..0x3 e `GetGamingSysInfo(0..2)` estão nos arquivos de evidência. `GetGamingProfile(0)` -> `0x00FF000001000000`. Não usados pelo app.
