# Pesquisa: suporte do SenseMonitor a notebooks gamer Acer em geral (2026-11-17)

Escopo: leitura e pesquisa na web. Nenhuma escrita no firmware foi feita. Chamadas WMI não foram executadas (o processo não estava elevado); só foram lidos o esquema das classes WMI (`Get-CimClass`), serviços, drivers e o log de erros deste PC.

Convenções: `[Sn]` aponta para a lista de fontes no fim. **(inferência)** = dedução minha a partir das fontes. **(NÃO VERIFICADO)** = relato isolado, só de busca ou sem teste em hardware.
Máquina de referência: Acer Nitro ANV15-51, BIOS V1.60 [S36].

---

## 0. Resumo executivo (o que muda no desenho do SenseMonitor)

1. **Os códigos que usamos são os mesmos do driver do Linux.** GetGamingSysInfo, SetGamingFanBehavior, SetGamingFanSpeed e Get/SetGamingMiscSetting vêm da classe `AcerGamingFunction`, GUID `7A4DDFE7-5B5D-40B4-8595-4408E0CC7F56` [S1][S36]. Vários projetos de Windows usam as mesmas codificações [S22][S23][S24][S26][S27].
2. **Dá para descobrir parte das capacidades pelo próprio firmware, só com leitura.** Dois bitmaps existem:
   - `GetGamingSysInfo(0)`: bits 24–39 são os sensores suportados. O bit `id-1` marca o sensor `id` [S1].
   - `GetGamingMiscSetting(0x0A)`: bitmap de perfis suportados, o bit N vale para o perfil de código N [S3][S5].
   - **Os dois bitmaps casam com a evidência deste PC** (ver 4.2).
3. **O bitmap de perfis não é confiável em todos os modelos.** O kernel parou de usá-lo no 6.17 porque o firmware de alguns modelos devolve dados inválidos [S8]. Tratar como dica, validar contra o perfil atual e contra uma tabela de modelos.
4. **Não há detecção segura de RGB e do tipo de teclado.** Em modelos de 2025 o firmware aceita escritas de RGB sem erro e os LEDs não mudam, porque o RGB está num controlador I2C-HID [S4][S21]. RGB depende de tabela por modelo mais enumeração HID.
5. **O número de ventoinhas e o suporte a controle também têm exceções.** O PHN14-51 usa outros IDs de sensor e outro caminho para a RPM [S3]. O ANV15-52 só aplica perfil por memória compartilhada do EC [S3][S32]. Não dá para assumir o conjunto de IDs do ANV15-51.
6. **Fabricar uma "lista branca" por DMI continua sendo o padrão.** O kernel só ativa o recurso gamer quando o `DMI_PRODUCT_NAME` está na tabela de quirks, ou com o parâmetro `predator_v4=1` [S1]. O mesmo vale para o Linuwu-Sense [S3].
7. **Instabilidade (BSOD):** não achei uma causa documentada que ligue NitroSense/PredatorSense a um bugcheck específico. Há relatos anedóticos em fóruns (seção 6). Neste PC houve bugchecks variados (0xA, 0x7E, 0xEF, 0x1C7) entre 10/11 e 17/11 [S36], padrão que não aponta para um único driver **(inferência)**.

---

## 1. Driver `acer-wmi` do kernel Linux

Fonte principal: `drivers/platform/x86/acer-wmi.c`, baixado em 2026-11-17 do branch master e das tags v6.8, v6.12 a v6.19, v7.0 e v7.1 [S1][S2]. As datas do master são posteriores à v7.1.

### 1.1 GUIDs

| GUID | Nome no código | Uso | Fonte |
|---|---|---|---|
| `7A4DDFE7-5B5D-40B4-8595-4408E0CC7F56` | WMID_GUID4 | Interface gamer (Windows: classe `AcerGamingFunction`) | [S1][S36] |
| `6AF4F258-B401-42FD-BE91-3D4AC2D7C0D3` | WMID_GUID1 | Brilho, Wi-Fi, Bluetooth, 3G | [S1] |
| `95764E09-FB56-4E83-B31A-37761F60994A` | WMID_GUID2 | Consulta de dispositivos (query block) | [S1] |
| `61EF69EA-865C-4BC3-A502-A0DEBA0CB531` | WMID_GUID3 | Windows: classe `APGeAction` (Get/SetFunction). Também hotkeys e "status do app Acer" | [S1][S36] |
| `676AA15E-6A47-4D9F-A2CC-1E6D18D14026` | ACERWMID_EVENT_GUID | Eventos (Windows: `APGeEvent`, campo `EventDetail`) | [S1][S36] |
| `79772EC5-04B1-4BFD-843C-61E7F77B6CC9` | GUID5 (só no Linuwu e no driver de bateria) | Controle de bateria (Windows: classe `BatteryControl`) | [S3][S14][S36] |

Classes Acer em `root\WMI` neste PC (somente esquema): `AcerGamingFunction`, `AcerGenericMethod` (GUID `1EA0BC57-521E-461B-A7A9-5D36F141A82A`, métodos SetFunction/GetFunction/GetStorageInformation), `AcerBiosConfigurationTool` (SetBiosOptions e afins), `APGeAction`, `APGeEvent`, `AcerGenericEvent`, `BatteryControl` [S36].

### 1.2 Métodos de `AcerGamingFunction` (ID do método no GUID4)

Do kernel [S1]:

| ID | Método | Observação |
|---|---|---|
| 2 / 4 | SetGamingLED / GetGamingLED | LED do botão Turbo. Get usa entrada `0x1`; Set liga `0x10001`, desliga `0x1` [S1][S10] |
| 5 | GetGamingSysInfo | Entrada u32 = comando (ver 1.3) |
| 14 / 15 | SetGamingFanBehavior / GetGamingFanBehavior | Ver 1.5 |
| 16 / 17 | SetGamingFanSpeed / GetGamingFanSpeed | Ver 1.5 |
| 22 / 23 | SetGamingMiscSetting / GetGamingMiscSetting | Perfil, OC, bitmap de perfis (ver 1.4) |

Só no Linuwu-Sense (engenharia reversa do PredatorSense) [S3]:

| ID | Método | Observação |
|---|---|---|
| 1 / 3 | SetGamingProfile / GetGamingProfile | "LCD override": Set `0x10` (desliga) ou `0x1000000000010` (liga). Aparece na lista de métodos da classe |
| 6 / 7 | SetGamingRgbKb / GetGamingRgbKb | Cor por zona (4 zonas) |
| 20 / 21 | SetGamingKBBacklight / GetGamingKBBacklight | Efeitos do teclado. Entrada de 16 bytes: `{modo, velocidade, brilho, 0, direção, R, G, B, 3, 1, 0...}` |

A lista completa de métodos da classe neste PC (do MOF, schema local) inclui também SetGamingProfileSetting/Get, SetGamingLEDBehavior/Get, SetGamingLEDColor/Get, SetGamingFanTable/Get e Set/GetCPUOverclockingProfile [S36]. Não achei codificação documentada para esses últimos; não usar.

### 1.3 `GetGamingSysInfo` (comandos e sensores)

- Entrada u32. Comandos conhecidos: `0x0000` = bitmap de sensores suportados; `0x0001 | (id << 8)` = leitura de sensor; `0x0002` = status da bateria/tomada [S1].
- Saída: byte 0 = status (0 = ok). Leitura em bits 8–23. Bitmap de sensores em bits 24–39 [S1].
- IDs de sensor no mainline [S1]:

| ID | Sensor |
|---|---|
| 0x01 | Temperatura da CPU |
| 0x02 | Rotação do ventilador da CPU |
| 0x03 | Temperatura externa 2 ("sistema") |
| 0x06 | Rotação do ventilador da GPU |
| 0x0A | Temperatura da GPU |

- Visibilidade no hwmon do kernel: o sensor `id` aparece se o bit `id-1` do bitmap estiver ligado [S1]. O driver de comunidade usa o mesmo comando 0 [S22].
- **Exceção PHN14-51** (Linuwu-Sense, comunidade): IDs de leitura 0x19 (ventilador CPU), 0x1A (ventilador GPU), 0x1D, 0x1E, 0x21 (temperaturas); a RPM dos ventiladores é lida direto do EC (offsets 0x13–0x16), porque "WMI sensor reading doesn't work on PHN14-51" no comentário do código [S3]. **(NÃO VERIFICADO em hardware por mim)**.
- `GetGamingSysInfo(0x02)` (status da bateria/tomada): o mainline define o comando, e o Linuwu e o patch do PHN16-71 o usam para saber se está na tomada (resultado diferente de zero = na tomada) [S1][S3][S9]. Isto reinterpreta a evidência deste PC (ver seção 8).

### 1.4 `GetGamingMiscSetting` / `SetGamingMiscSetting`

Entrada: `índice | (valor << 8)` no Set; `índice` no Get. Saída: status no byte 0, valor no byte 1 [S1].

| Índice | Significado | Fonte |
|---|---|---|
| 0x05 | OC 1 (GPU): 0 normal, 2 turbo (Set `0x205`/`0x5`) | [S1][S10][S24] |
| 0x07 | OC 2 (CPU, segundo ajuste): mesmos valores | [S1][S10][S25] |
| 0x0A | Bitmap de perfis suportados. Marcado "Unreliable on some models" no kernel | [S1][S8] |
| 0x0B | Perfil atual / Set perfil: `0x0B | (perfil << 8)` | [S1][S9] |

Perfil de plataforma (mesmo mapa em mainline e Linuwu) [S1][S3][S9]:

| Código | Perfil Acer | `platform_profile` do Linux | Valor do Set (`0x0B|perfil<<8`) |
|---|---|---|---|
| 0x00 | Quiet (silencioso) | quiet | `0x000B` |
| 0x01 | Balanced | balanced | `0x010B` |
| 0x04 | Performance | balanced-performance | `0x040B` |
| 0x05 | Turbo | performance | `0x050B` |
| 0x06 | Eco | low-power | `0x060B` |

Como o kernel descobre os perfis suportados (histórico) [S2][S8]:
- v6.8 a v6.13: lista fixa de perfis.
- v6.14 a v6.16: lê `GetGamingMiscSetting(0x0A)` e liga cada perfil cujo bit está setado.
- v6.17 em diante (e backport em 6.16-stable): **volta à lista fixa** (Performance, Balanced-Performance, Balanced, Quiet, Low-Power), com o comentário "Unreliable on some models". Motivo no commit: firmware de alguns modelos devolve dados inválidos para o bitmap e isso impedia o driver de carregar; o autor quer entender como o software OEM detecta os perfis [S8].
- Contraexemplo: no ANV15-51 o bitmap lê `0x53` (bits 0, 1, 4, 6 = Quiet, Balanced, Performance, Eco) e **escrever Turbo é rejeitado pelo firmware**; os outros quatro funcionam e alteram o ruído das ventoinhas [S5]. Isso bate com a leitura `0x5300` neste PC (seção 4.2).
- Em modelos sem botão Turbo, o evento de tecla de modo vem como chave 0x4 [S1].
- Na tomada vs. bateria: o Linuwu bloqueia Performance, Balanced-Performance e Quiet na bateria, imitando o app oficial ("not supported when its not plugged in AC") [S3]. **(NÃO VERIFICADO no app oficial)**.

Modelos que fogem desse caminho de perfil:
- **PH315-53**: `platform_profile` dá EIO; o caminho é o Turbo (LED + ventoinha + OC 5/7) [S24][S10].
- **ANV15-52 (BIOS V1.53 relatada)**: leitura/escrita do perfil falha com EIO; o Linuwu aplica o perfil escrevendo no EC por janela de memória física `0xFE708000` (offset `0x607`) e chamando `\_SB.PC00.LPCB.EC0._Q30`. Em BIOS V1.31 o caminho WMI funcionava. **Dependente de BIOS** [S3][S32]. **Não recomendo replicar** (acesso a memória física).
- **AN515-44/45** (Nitro 5 antigo, `nitro_sense=2` no Linuwu): sem `platform_profile`; só ventoinhas e CoolBoost [S3][S20][S37].

### 1.5 Ventoinhas

- `SetGamingFanBehavior` (método 14), entrada u64: bits 0–15 = bitmap de ventoinhas (CPU = bit 0 = `0x1`, GPU = bit 3 = `0x8`); bits 16–17 = modo da CPU; bits 22–23 = modo da GPU. Modos: 1 Auto, 2 Turbo/Max, 3 Custom [S1][S11].
  - Auto/Auto `0x410009`, Max/Max `0x820009`, Custom/Custom `0xC30009` [S3][S22][S24][S26].
  - Só a CPU em custom: `0x30001`; só a GPU em custom: `0xC00008` [S23][S24][S3].
- `GetGamingFanBehavior` (método 15): entrada = bitmap consultado, **nunca 0** (retorna status 2, que parece "sem suporte" mas não é) [S23]. Saída: status no byte 0; modo da CPU nos bits 8–9, da GPU nos bits 14–15 [S1][S23]. Coincide com a evidência deste PC (`0x4100`) [S36].
- `SetGamingFanSpeed` (método 16): `idVentoinha | (pct << 8)`, pct 0–100; CPU = 1, GPU = 4. Status 0 ok, 1 = ventoinha inexistente (-ENODEV), 2 = valor inválido [S1][S12]. O kernel diz que esta chamada passa a ventoinha a manual automaticamente (relato do mantenedor) [S13].
- `GetGamingFanSpeed` (método 17): `id` na entrada; percentual nos bits 8–15; **sempre 0% em Auto**, só reflete o duty em Custom [S27][S1]. Status nonzero = ventoinha inexistente [S12].
- Ordem para uma ventoinha Custom e outra Turbo (relato): setar `0x820009`, depois `0x30001` para rebaixar só a CPU, depois `0x6401` [S23].
- Quantidade de ventoinhas: o kernel usa `cpu_fans`/`gpu_fans` por modelo só para montar o bitmap do Turbo (`WMID_gaming_set_fan_mode`) [S1]. Um mantenedor disse ter achado como detectar o número de ventoinhas automaticamente, sem detalhar [S13]; outro perguntou se existe operação WMI para consultar o bitmap de ventoinhas, sem resposta [S11]. Meio prático hoje: bits 1 (id 2) e 5 (id 6) do bitmap de sensores, mais status 0 em `GetGamingFanSpeed(1)` e `GetGamingFanSpeed(4)` **(inferência)**.
- Tabela de ventoinha: `SetGamingFanTable` sem codificação conhecida; `GetGamingFanTable` devolve status 2 em AN515-43 (sem tabela no firmware) [S26] e `0x1` neste PC [S36].

Comportamentos de firmware que afetam o app:
- A configuração Custom pode ser sobrescrita pelo firmware após suspensão, retomada ou troca de perfil; ferramentas reaplicam periodicamente [S22].
- Aspire A515-57: o modo setado pelo WMI às vezes volta em cerca de 2 s mesmo com leitura imediata correta; precisa reaplicar (relato de 1 usuário) [S23].
- PH315-53/PT315: a ventoinha volta para Auto no reinício e quando o controlador sai [S25].
- AN515-43: em Custom o piso é ~3950 RPM, acima do ~2500 RPM do Auto; ou seja, Custom não fica mais silencioso que Auto [S26].
- **Perigo (PH315-53, Turbo):** rodar Turbo com a bateria removida pode derrubar o notebook sob carga [S24].

### 1.6 Bateria (health mode) e USB charging

Bateria (`BatteryControl`, GUID `79772EC5-...`, no Linux só no driver `acer-wmi-battery`, em revisão para entrar no mainline) [S14][S15]:
- Método 20 `GetBatteryHealthControlStatus`: entrada `battery_no=1`, `function_query=1`, `reserved[2]=0`. Saída: `uFunctionList` (bit 0x01 = modo saúde suportado, bit 0x02 = calibração suportada), `uFunctionStatus[0]` = saúde ligada, `[1]` = calibração ligada [S14][S3].
- Método 21 `SetBatteryHealthControl`: `battery_no=1`, `function_mask` (1 saúde, 2 calibração), `function_status` (1 liga, 0 desliga) [S14].
- Método 19 `GetBattInfoInterface`: índice `0x8` e bateria `1` devolve temperatura da bateria [S14].
- Saúde = limita a carga a ~80%. Calibração desliga o modo saúde, carrega a 100%, descarrega e recarrega; **não ligar sem pedido explícito do usuário** [S15].
- A interface não existe em todos os Acer; o driver só habilita o controle quando `uFunctionList` tem o bit de saúde [S14].
- O kernel patch v7 tem allowlist DMI com Nitro ANV15-51, AN515-57, AN515-58, AN517-54 e Predator PHN16-71 [S14]. A lista de modelos do projeto de origem confirma "funciona" para ANV15-51, AN515-57/-58, AN517-54, AN16-43, ANV16-42, PHN16-71, Triton 300 SE, Aspire 7 A715-42G; "não funciona" para AN515-47 e vários Aspire/TravelMate [S16].
- No Predator "legacy" PH315-52 o limitador não é suportado e o estado lido pode não refletir a escrita [S3].
- Um testador do ANV15-52 relatou o limite de 80% persistindo após reinício [S30] **(NÃO VERIFICADO além de 1 relato)**.

USB charging ("carregar via USB com notebook desligado") [S3]:
- Usa `APGeAction` (GUID3) `GetFunction(0x4)` e `SetFunction(...)`, **não** o GUID gamer.
- Get devolve `663296`, `659200`, `1314560` ou `1969920` para desligado, 10%, 20%, 30%; Set usa `663300`, `659204`, `1314564`, `1969924`. Decodificação: byte 0 = função 4, byte 2 = limiar (0x0A, 0x14, 0x1E). **(inferência, só os números do código)**.
- Não achei confirmação de que ANV15-51 tenha esse recurso.

Outros (Linuwu): `APGeAction.SetFunction(0x07 | on<<16)` = CoolBoost, só funciona em Auto [S20]. O OpenPredatorSense relata CoolBoost por `AcerGenericMethod::SetFunction` [S28]: **duas classes diferentes citadas; não está claro qual vale para cada modelo (NÃO VERIFICADO)**.

### 1.7 Tabela DMI de quirks: mainline (master)

O mainline hoje só tem os campos `turbo`, `cpu_fans`, `gpu_fans`, `hwmon`, `platform_profile`, `pwm` (mais os antigos de Wi-Fi/brilho). **Os nomes `predator_v4`, `nitro_v4`, `nitro_sense` e `four_zone_kb` não existem no mainline atual.** Até a tag v7.1 existe um único campo `predator_v4` (mais `pwm` a partir da v6.19); só o master, depois da v7.1, o dividiu em `hwmon` e `platform_profile` [S2][S1]. Os nomes `nitro_v4`, `nitro_sense` e `four_zone_kb` são do Linuwu-Sense [S3]. A matriz abaixo foi montada por grep nos arquivos de cada tag [S2].

Evolução por tag (modelos gamer):

| Versão | O que aparece | Fonte |
|---|---|---|
| v6.8 | PH315-53 (Turbo), PHN16-71 (perfil + tecla de modo) | [S2][S9][S10] |
| v6.12–v6.13 | + PH16-71, PH18-71 | [S2] |
| v6.14–v6.16 | + AN515-58, PH16-72, PT14-51; leitura de sensores (bitmap) e RPM no hwmon; perfil via bitmap 0x0A | [S2] |
| v6.17–v6.18 | Perfil volta à lista fixa (bitmap "unreliable") | [S2][S8] |
| v6.19, v7.0, v7.1 | + PHN16-72; controle de ventoinha (`pwm1`, `pwm1_enable`) | [S2][S12] |
| master (após v7.1) | + AN16-41, + ANV15-51, + A315-58G; quirk separado `hwmon`/`platform_profile` | [S1] |

Quirks atuais do master [S1]:

| Modelo (DMI_PRODUCT_NAME) | Flags |
|---|---|
| Nitro AN515-58, Nitro AN16-41 | hwmon, platform_profile, pwm |
| **Nitro ANV15-51** | cpu_fans=1, gpu_fans=1, hwmon, platform_profile, pwm (sem turbo) |
| Predator PH315-53 | turbo, cpu_fans=1, gpu_fans=1 (sem profile/hwmon) |
| Predator PH16-72, PHN16-72 | turbo, cpu_fans=1, gpu_fans=1, hwmon, platform_profile, pwm |
| Predator PT14-51 | turbo, cpu_fans=1, gpu_fans=1, hwmon, platform_profile, pwm |
| Predator PHN16-71, PH16-71, PH18-71 | hwmon, platform_profile (sem pwm) |
| Aspire A315-58G | só hwmon |
| `predator_v4=1` (parâmetro) | hwmon, platform_profile |

Patch do ANV15-51 (julho de 2026): modelo usa a interface Predator V4 para perfil e hwmon, sem quirk antes; o autor confirmou 2 ventoinhas e temperatura via hwmon e que **Turbo é rejeitado**; o revisor pediu uma entrada própria (`cpu_fans`, `gpu_fans`, `pwm`) sem turbo, e o autor ainda não testou controle PWM quando postou [S5][S6][S7]. O master já traz a entrada com `pwm=1` [S1].

Teclas gamer no kernel [S1][S9][S10]:
- Evento 0x7 = `WMID_GAMING_TURBO_KEY_EVENT`. `key_num` 0x4: se o modelo tem LED de Turbo (`ACER_CAP_TURBO_LED`), alterna Turbo (LED + ventoinha Turbo + OC 5/7); senão, troca o perfil. `key_num` 0x5: troca o perfil (se há `platform_profile`).
- O parâmetro `cycle_gaming_thermal_profile` (padrão ligado) faz a tecla ciclar perfis; desligado, ela alterna Turbo/último perfil [S9][S1].
- Evento 0x8 = mudança de tomada. Linuwu: `key_num` 0 = ligado na tomada, 1 = desligado [S3].
- Evento 0x84 do mapa de hotkeys aparece sozinho em transições do backlight do teclado; não mapear [S30].

---

## 2. Linuwu-Sense: tabela de quirks (mais completa que o mainline)

Fonte: `src/linuwu_sense.c`, branch main, 2026-11-17 [S3]. É um fork do acer-wmi, mantido por voluntários e orientado a Nitro/Predator.

Significado dos flags [S3]:

| Flag | Efeito no código |
|---|---|
| `predator_v4=1` | platform_profile + leitura de ventoinha + interface PredatorSense |
| `predator_v4=2` | PredatorSense v3: ventoinha + interface, **sem** platform_profile (firmware não tem os métodos) |
| `nitro_sense=1` | platform_profile + ventoinha + interface Nitro |
| `nitro_sense=2` | Nitro antigo: **sem** platform_profile |
| `nitro_v4=1` | tudo do v4 (platform_profile + ventoinha + NITRO_SENSE_V4) |
| `four_zone_kb` | cria `four_zoned_kb` (RGB de 4 zonas) |
| `turbo`, `cpu_fans`, `gpu_fans` | como no mainline |
| `phn14_sensors`, `ec_mmio_profile`, `nitro_hid_kb`, `an515_58_rgb_fix`, `kb_zone_arm`, `predator_legacy` | exceções por modelo (IDs de sensor do PHN14, perfil via EC no ANV15-52, RGB por HID, correção de bytes de RGB, "armar" as zonas no probe) |

Modelo para quirk (os modelos da primeira tabela DMI do arquivo; se um modelo aparece duas vezes, o primeiro casamento vence porque a callback devolve 1) [S3]:

| Modelo | Quirk |
|---|---|
| Nitro ANV15-41, **ANV15-51** | `nitro_sense=1` (perfil + ventoinha), sem four_zone |
| Nitro ANV15-52 | `nitro_v4=1` + `ec_mmio_profile=1` |
| Nitro ANV16-41, AN16-41, AN16-42, AN16-43, AN17-51, AN517-41 | `nitro_v4=1` + `four_zone_kb=1` |
| Nitro ANV16S-41 | `nitro_v4` + `four_zone_kb` + `nitro_hid_kb=1` (RGB via HID) |
| Nitro ANV16-71 | `nitro_v4`, turbo=1, cpu_fans=1, gpu_fans=1, brightness=1 |
| Nitro AN515-58 | `nitro_v4` + `four_zone_kb` + `an515_58_rgb_fix` |
| Nitro AN515-57 | `nitro_v4` só (sem four_zone: teclado vermelho fixo, segundo PR) [S37] |
| Nitro AN515-55 | `nitro_sense=1` + `four_zone_kb` + `an515_58_rgb_fix` (primeiro casamento) |
| Nitro AN515-54 | `nitro_v4`, turbo=1, cpu_fans=1, gpu_fans=1 (GPU de 1650 não aparece; comentário diz que Auto não funciona para GPU) |
| Nitro AN515-44, AN515-45 | `nitro_sense=2` + `four_zone_kb` (+ rgb_fix no 44) |
| Nitro AN517-54 | `nitro_v4` (genérico) |
| Predator PH315-52, PH315-53 | `predator_v4=1`, turbo, cpu_fans=1, gpu_fans=1, four_zone_kb, kb_zone_arm |
| Predator PH315-54 | `predator_v4=1`, four_zone_kb, turbo, 1+1 ventoinhas |
| Predator PHN16-71, PHIN16-71, PHN16-72, PHN16-73, PHN16S-71, PHIN16S-71 | `predator_v4=1` + `four_zone_kb=1` |
| Predator PHN14-51 | `predator_v4` + `four_zone_kb` + `phn14_sensors=1` |
| Predator PH18-71, PH16-72, PH18-73, PTX17-71 | `predator_v4=1` (genérico, sem four_zone) |

Divergências entre fontes para o mesmo modelo (**tratar com cautela**):
- **AN515-45**: um PR usa `nitro_sense=2` + `four_zone_kb` ("legacy"); relata que forçar `nitro_v4` dá controle de ventoinha mas quebra o registro de `platform_profile`; um guia de outro projeto usa `nitro_v4=1` [S37][S19].
- **PH315-53**: o comentário do código registra `predator_v4=1` (um PR) contra `predator_v4=2` (outro PR). Há uma entrada "experimental" que testa a interface v4 nos PH315-52/53 porque "has never been confirmed to exist on PH315-53/PH315-52 firmware" [S3]. O relato do rawPSense confirma que `platform_profile` dá EIO no PH315-53 [S24].

Guia de compatibilidade do DAMX (GUI do Linuwu): ANV15-51 "totalmente suportado, testado oficialmente"; ANV15-41, AN16-41/43, PHN16-71/72, PH16-71, PH18-71, PH315-53 suportados; AN515-44/-47/-58 e AN517-54 "precisam forçar `nitro_v4`"; AN517-55 não verificado [S18].

---

## 3. Projetos da comunidade

### 3.1 Windows (usam `root\WMI` / `AcerGamingFunction`)

| Projeto | O que faz / quais códigos | Modelos citados | Pontos de atenção |
|---|---|---|---|
| [S22] reyi06/fancurve | Curva de ventoinha em software. `GetGamingSysInfo`: `0x0101` CPU temp, `0x0201` RPM CPU, `0x0301` temp sistema, `0x0601` RPM GPU, `0x0A01` GPU temp, `0x0000` bitmap. Modo/velocidade como em 1.5. IDs de ventoinha configuráveis (`null` desliga a ventoinha, p.ex. modelos só com CPU) | Testado em AN515-57 (BIOS V1.22). Diz que modelos antigos podem ter só o controle de ventoinha, sem SysInfo | Reaplica a curva (o firmware sobrescreve após retomada/troca de perfil). Falha de leitura 6x seguidas → ventoinhas a 70%; 3 falhas de escrita → volta a Auto. Fallback por UI Automation do NitroSense. Não rodar com outro controlador de ventoinha |
| [S23] vicentetob/acer-aspire-fan-control | Mesmas chamadas em **Aspire 5 A515-57** (não-gamer): o firmware responde ao modo Turbo escondido | A515-57 verificado; A515-57G/-56/-54/-43/A517-53G "plausível, não verificado" | **Setters só funcionam em chamada por instância**; chamada estática dá "Invalid method parameter(s)". Getters aceitam estática. Getter precisa de bitmap ≠ 0. Modo volta após ~2 s em um caso. Ordem importa para CPU custom + GPU turbo |
| [S24] joserodpt/rawPSense | PH315-53: LED `0x10001`/`0x1`; ventoinhas `0x820009`/`0x410009`; OC `0x205`+`0x207` / `0x5`+`0x7`; custom CPU `0x30001`, GPU `0xC00008` | PH315-53 | Aviso de bateria removida; no Linux `platform_profile` dá EIO |
| [S25] romrepostacks/REA-predatorsense-TOOL | Mesmas codificações; **sonda o BIOS antes de setar**; GPU OC = misc 5; cita misc 7 como nível de OC da CPU no PredatorSense e não mexe nele | Triton 300 PT315; controle de ventoinha só confirmado no PT315-52 | Ventoinha volta para Auto no reinício; curva força 100% a 92 °C |
| [S26] charllysb/nitro-fan | Auto `0x410009`, Max `0x820009`, Manual `0xC30009`; velocidade `id|(%<<8)`. Mede RPM. `GetGamingFanTable` devolve 2 | AN515-43 | PowerShell: `Invoke-CimMethod` devolve `PSCustomObject`; ler `$r.gmOutput` (ler `OutParameters` dá `$null`) |
| [S27] Flybaby1/predator-control | Mesmo mapa; sensor `0x03` = temperatura externa 2 | PHN16-72 (BIOS V1.17) | `0x80070057` = chamada estática (nível de classe); acesso negado sem admin. "PredatorSense pode dizer 'sem hardware'" pelos mesmos erros. `GetGamingFanSpeed` = 0% em Auto. Custom conflita com PredatorSense |
| [S28] Xenonesis/Openpredator-Sense | Substituto do PredatorSense (Rust/Tauri). Usa `AcerGamingFunction` e `AcerGenericMethod`; `SetGamingMiscSetting` com "Quiet, Default, Extreme, Turbo"; `SetGamingRgbKb` (4 zonas) e `SetGamingKBBacklight` (efeitos); `SetGamingProfile` (tecla Windows, overdrive); `AcerGenericMethod::SetFunction` (CoolBoost). Não depende de PSSvc.exe, PSAgent.exe, PSAdminAgent.exe | Não lista modelos | Sem seção de limitações; **não validado**; faixa de ventoinha 30–100% |
| [S29] krwclassic/nitrosensual | Não usa WMI direto: grava em chaves de registro do NitroSense e fala com o serviço PSsvc | Nitro (não testou Predator) | Exige NitroSense instalado, em modo Custom, e PSsvc rodando; "abusa do NitroSense" |

### 3.2 Linux

| Projeto | Papel | Fonte |
|---|---|---|
| Linuwu-Sense (0x7375646F) | Fork do acer-wmi com RGB de 4 zonas, ventoinha, bateria, USB, LCD override, perfis por tomada/bateria persistidos | [S3][S4] |
| facer / acer-predator-turbo-and-rgb-keyboard-linux-module (Jafar Akhondali) | Origem do Turbo e RGB. Turbo+RGB testados em PH315-52/-53/-54, PH317-53, PHN18-71, PT315-51, PT316-51, PT515-51; só RGB em AN515-45/-55/-56/-57/-58, AN517-41, PT314-51; Turbo "implementado, não testado" em PH317-54, PH517-51/-52, PH717-71/-72, PT315-52, PT314-52s, PT316-51s, PT515-52, PT516-52s, PT917-71. Detecção: existência de `/sys/bus/wmi/devices/7A4DDFE7-...`. Parâmetro `rgb_ec_enable=1` liga a flag "PredatorSense em execução" no EC | [S17] |
| DAMX (PXDiv) | GUI sobre o Linuwu. Daemon detecta recursos por modelo e **esconde o que não é suportado**; Turbo some na bateria | [S18] |
| AcerSense (kleqing) | Fork do Linuwu; FAQ cita os quirks `predator_v4`/`nitro_v4`/`nitro_sense`, `four_zone_kb`, `cpu_fans`/`gpu_fans`, `enable_all` | [S19] |
| nitrosense-linux (napejoon) | Em AN515-44: ventoinha (método 14/16), sensores (método 5), RGB (`SetGamingRgbKb` = método 6 com bits de zona 1/2/4/8 + R,G,B; `SetGamingKBBacklight` = método 20). Em ACPI desse BIOS, os métodos 5, 6, 7, 16, 17, 20, 21 existem em SSDT5 e os IDs 14, 22, 23 casam com o mainline | [S20] |
| acer-wmi-battery (frederik-h) | Health mode e calibração | [S15][S16] |
| kbrgb (shwetankg07) | RGB do controlador ENE KB5130 por feature report HID de 11 bytes | [S21] |

### 3.3 Diferenças e armadilhas por modelo

- **ENE KB5130 (I2C-HID, VID `0CF2` PID `5130`)**: PHN16S-71, ANV16S-41, AN16S-61 (confirmados) e PHN16-73 (esperado). Nesses modelos o firmware **aceita as chamadas WMI de RGB sem erro e os LEDs não mudam** [S4][S21]. O PHN16-72 usa outro controlador (`1025174B:6243`) [S21]. **Detecção só por WMI é impossível.**
- **LCD overdrive (AN515-44)**: o firmware aceita o comando e nada acontece; `Get` devolve `0xFF` = "não suportado" [S20]. Mesma convenção `0xFF` aparece no nosso `GetGamingMiscSetting(0x7)=0xFF00` [S36] **(inferência)**.
- **Timeout do backlight (AN515-44)**: o BIOS rejeita com `0xE2` [S20].
- **Backlight AN515-54**: ligar o timeout desliga as luzes até reiniciar (comentário no código) [S3].
- **Instalação do Linuwu no Windows**: não existe versão para Windows. Linux só.
- **Secure Boot** (Linux): bloqueia módulos não assinados [S17][S19]. Irrelevante para o Windows.
- **Escritas cegas "funcionaram" sem efeito**: ANV15-52 perfil WMI (ver 1.4), RGB das séries S, e o `SetGamingMiscSetting(0x10B)` já era o estado atual neste PC [S36]. **A confirmação por releitura só vale se o estado mudou.**

---

## 4. Detecção segura de capacidades (somente leitura)

### 4.1 Receita proposta, em ordem

0. **Identidade** (sem WMI do Acer): `Win32_ComputerSystem.Model` e `SystemFamily`; neste PC dá `Nitro ANV15-51` e família `Acer_Nitro_V_15` [S36]. É a mesma string de `DMI_PRODUCT_NAME` usada pelo kernel [S1]. Ler também a versão do BIOS (já houve comportamento diferente entre BIOS no ANV15-52) [S32].
1. **Classe presente?** `Get-CimClass root\WMI AcerGamingFunction` (só esquema, não exige admin). Instâncias exigem admin; sem elevação a classe vem sem instâncias [S36][S23].
2. **Bitmap de sensores**: `GetGamingSysInfo(0)` → status (byte 0) e `(raw >> 24) & 0xFFFF` [S1]. Mapear cada bit para o ID (`bit+1`). Ler cada sensor suportado com `0x01|(id<<8)`.
   - Se o modelo for da família PHN14-51, esperar IDs diferentes (0x19.., RPM por EC) [S3]. **(NÃO VERIFICADO)**
3. **Ventoinhas presentes**: bits 1 e 5 do bitmap (CPU/GPU fan) e `GetGamingFanSpeed(1)`, `GetGamingFanSpeed(4)` com status 0 [S1][S12]. IDs inexistentes devolvem status 1 [S12]; nesta máquina `GetGamingFanSpeed(2)` e `(8)` devolveram 1 [S36].
4. **Controle de ventoinha possível**: `GetGamingFanBehavior(0x1)`, `(0x8)` e `(0x9)` com status 0 e modo entre 1 e 3 [S1]. Nunca chamar com 0 [S23].
5. **Perfis**: `GetGamingMiscSetting(0x0A)` → byte de valor = bitmap; `GetGamingMiscSetting(0x0B)` → perfil atual. Aceitar o bitmap somente se (a) status 0, (b) o bit do perfil atual está ligado, (c) o bitmap não é 0 nem 0xFF. Caso contrário usar a lista por modelo [S8]. Perfis fora do bitmap nunca devem ser oferecidos (ex.: Turbo no ANV15-51 é rejeitado) [S5].
6. **OC / Turbo de LED (família PH315-53, PT14-51, PH16-72)**: `GetGamingMiscSetting(0x05)` e `(0x07)` com status 0, e `GetGamingLED(0x1)` [S1][S10]. Neste PC `(0x5)` devolve status 1 (não suportado) [S36]. Resultado "status 0" em outros modelos **(NÃO VERIFICADO, só plausível)**.
7. **Bateria**: `BatteryControl.GetBatteryHealthControlStatus(1, 1, [0,0])`; suportado se `uFunctionList & 1` (saúde) e `& 2` (calibração) [S14].
8. **USB charging**: `APGeAction.GetFunction(0x4)` e ver se o resultado é um dos quatro números conhecidos (1.6) [S3]. **(NÃO VERIFICADO fora do Linuwu)**.
9. **RGB / teclado**: não há leitura confiável. Fazer: (a) tabela por modelo (seção 7); (b) enumerar HID `VID_0CF2&PID_5130` (ENE KB5130) [S21]; (c) `GetGamingKBBacklight` só como informação, nunca como prova de suporte, porque as séries S "aceitam tudo" [S4].
10. **Eventos / tecla**: ver seção 5. Inscrever-se em `root\WMI:APGeEvent` (campo `EventDetail`) é só leitura; a classe existe aqui, mas não testei a assinatura **(NÃO VERIFICADO)** [S36].

Regras gerais:
- Chamar métodos **por instância**; chamada estática dá `0x80070057` nos Set e, em alguns Get, status 2 [S23][S27].
- Tratar status ≠ 0 como "não suportado" sem insistir; pausar entre chamadas (o firmware responde de forma síncrona e já devolveu valores inconsistentes em alguns modelos) **(inferência)**.
- No PowerShell, ler `gmOutput` da saída; `OutParameters` vem `$null` [S26].
- Registrar no diário (B02) cada leitura, para comparar entre modelos.

### 4.2 Evidência deste PC lida com o que a pesquisa trouxe

Do `docs/probe-idle.txt` [S36]:

| Leitura | Valor | Decodificação |
|---|---|---|
| `GetGamingSysInfo(0x0)` | `0x0000000227000000` | Bitmap de sensores = `0x0227` = bits 0, 1, 2, 5, 9 = IDs 1, 2, 3, 6, 10 = CPU temp, CPU fan, temp externa 2, GPU fan, GPU temp. **Casa exatamente com os IDs que usamos, mais o 0x03** |
| `GetGamingSysInfo(0x3..0x5)` | `0x2` | Comandos inválidos: só existem 0, 1 e 2 |
| `GetGamingMiscSetting(0xA)` | `0x5300` | Bitmap de perfis `0x53` = perfis 0, 1, 4, 6: quiet, balanced, performance, eco. **Turbo (5) ausente**, igual ao relato do ANV15-51 no kernel [S5] |
| `GetGamingMiscSetting(0xB)` | `0x100` | Perfil atual = 1 (balanced) |
| `GetGamingMiscSetting(0x5)` | `0x1` | Status 1 = OC da GPU não suportado |
| `GetGamingMiscSetting(0x7)` | `0xFF00` | Status 0, valor `0xFF` (provável "não suportado") |
| `GetGamingFanBehavior(0x1/0x8/0x9)` | `0x100 / 0x4000 / 0x4100` | CPU e GPU em Auto; leitura em `<<8` e `<<14` |
| `GetGamingFanSpeed(1/4)` | `0` | Ventoinhas existem (status 0), valor 0 em Auto |
| `GetGamingFanSpeed(2/8)` | `1` | Status 1 = id inexistente |

### 4.3 O que NÃO é seguro sondar

Qualquer `Set*` e os `Get*` cujo significado não está documentado:
- `SetGamingMiscSetting` com índices 0x05 e 0x07: altera OC de GPU/CPU; o rawPSense avisa que Turbo com a bateria removida pode derrubar o PH315-53 sob carga [S24][S10].
- `SetGamingFanTable`, `SetCPUOverclockingProfile`, `SetGamingProfileSetting`, `SetGamingLEDBehavior`, `SetGamingLEDColor`: sem codificação documentada em nenhuma fonte que achei.
- `SetGamingProfile` (LCD override, tecla Windows): efeito imediato e persistente.
- `AcerBiosConfigurationTool.SetBiosOptions` / `SetBiosDefaultOptions`: mexem em opções de BIOS [S36].
- `BatteryControl.SetBatteryHealthControl` / `SetBatteryFunctionData`: calibração descarrega a bateria por horas [S15].
- `APGeAction.SetFunction` e `AcerGenericMethod.SetFunction`: também carregam o "status do aplicativo Acer" (Launch Manager, ePM etc.) e CoolBoost; ligar ou desligar essa flag pode mudar o que o EC entrega ao SO [S1][S3][S17].
- Escritas de RGB: inofensivas, mas o resultado é enganoso (ver 3.3).
- `SetGamingFanBehavior` com bitmap 0 ou IDs de ventoinha inexistentes: erro de status; cuidado com a ordem das chamadas (ver 1.5).
- Como regra geral: o firmware aceita sem efeito, e a releitura só prova algo se o estado mudar [S36].

Chamadas `Get*` que já deram resultado inofensivo: nosso probe de leitura (`GetGamingSysInfo`, `GetGamingMiscSetting` 0..0x10, `GetGamingProfileSetting`, `GetGamingProfile`, `GetGamingFanBehavior`, `GetGamingFanSpeed`, `GetGamingFanTable`) rodou sem efeito colateral visível neste PC [S36]. **Em outros modelos, não há garantia.**

---

## 5. Tecla NitroSense / PredatorSense / Turbo

| Família | O que chega ao Windows/Linux | Fonte |
|---|---|---|
| Nitro (ANV15-51, ANV15-52, AN515-47, AN515-58, AN517-54, AN515-44) | **Scancode AT 0xF5**, não é evento WMI. No Linux: `KEY_PRESENTATION`/`prog1`. systemd hwdb: `KEYBOARD_KEY_f5=prog1` ("NitroSense button") para AN515-47, AN515-58, AN517-54, ANV15-51, ANV15-52 | [S33][S30][S20] |
| Nossa leitura (Windows) | vk `0xFF`, scan `0x75` (dado informado pela equipe, não reverificado aqui) | pedido da tarefa |
| Predator PHN16-71 | `KEYBOARD_KEY_f5=prog1` ("predator sense" button), mais tecla de modo por WMI | [S33] |
| Predator PHN16-72 | Sem entrada f5 no hwdb; só `66=micmute` | [S33] |
| Predator Helios 300 (PH315-53) e outros com botão Turbo | Evento WMI 0x7, `key_num` 0x4 (Turbo) | [S1][S10] |
| Modelos sem botão Turbo (tecla de modo) | Evento WMI 0x7, `key_num` 0x4 (modelos sem LED) ou 0x5 | [S1][S9] |
| Nitro V16 / ANV16-42 (teclado 1025:1825) | **Nenhum canal**: nem evdev, nem WMI, nem HID. Hipótese de quem testou: o EC só entrega a tecla depois do "aperto de mão" do app Acer **(NÃO VERIFICADO)** | [S31] |
| ANV16S-41, PHN16S-71 | Botão de hardware confirmado pelo DAMX (via HID) | [S18] |

Notas:
- 0xF5 do Linux e scan `0x75` do Windows são provavelmente o mesmo código com o bit alto (0x80 = bit de liberação em set 1) **(inferência)**.
- `KEYBOARD_KEY_f5=presentation` é o padrão geral do hwdb para Acer; os Nitro sobrescrevem para `prog1` [S33].
- Evitar mapear 0x84 (dispara sozinho com o backlight) [S30].
- Para o Turbo dos Predator, um gancho de teclado não basta (é evento ACPI-WMI). Seria preciso assinar `APGeEvent`/`EventDetail` **(NÃO VERIFICADO)** [S36].

---

## 6. Coexistência com serviços Acer e instabilidade

### 6.1 O que existe neste PC (observado)

Tudo `Stopped` e `Disabled` agora (plano único diz que foram desativados em 2026-11-17 10:00 durante a investigação da tela azul) [S36]:

| Serviço | Nome de exibição | Executável / pacote INF | Papel (fonte) |
|---|---|---|---|
| AASSvc | Acer Agent Service | `AcerCentralService.exe`, `predatorservice.inf` | Backend do NitroSense/PredatorSense **(inferência pelo nome do pacote)** |
| AcerLightingService | Acer Lighting Service | `AcerLightingService.exe`, `predatorservice.inf` | Iluminação do app **(inferência)** |
| ASMSvc | Acer System Monitor Service | `AcerSystemCentralService.exe`, `sysmonitorservice.inf` (componente `ACERDRIVERCOMPONENTGAMING`) | Monitoramento de sensores para o app **(inferência)** |
| AcerServiceSvc | Acer Service Component Service | `AcerServiceWrapper.exe`, `acerservicecomponent.inf` | Serviço genérico Acer **(inferência)** |
| AcerDeviceEnablingServiceV2 | Acer Device Enabling Sevice V2 | `ADESv2Svc.exe`, `acerdeviceenablingservicecomponent.inf` | Habilita recursos específicos do modelo (descrição de um usuário, não oficial) |

Drivers de kernel Acer em execução (Manual): `AcerApplicationBaseDriver_Device` (vinculado ao dispositivo ACPI `1025165F`, "Acer Application Base Software Component") e `AcerAirplaneModeController` (HID `ACPI\10251229`) [S36].

Fontes externas sobre os serviços: não achei documentação oficial. Há apenas descrições de usuários (AAADSvc "relacionado a gerenciamento de dispositivos e autenticação", ADES "gerencia recursos específicos do dispositivo") **(NÃO VERIFICADO)** [S34]. Em fóruns de remoção de malware aparecem os mesmos executáveis como entradas normais de driver/serviço Acer [S34].

Outro fato: projetos mais antigos citam `PSSvc.exe`, `PSAgent.exe`, `PSAdminAgent.exe` como serviços do PredatorSense [S28][S29]. Nomes diferentes dos nossos; versões diferentes do pacote.

### 6.2 Quebra hotkeys ou RGB ao remover?

- **Perfil, ventoinha e sensores: não dependem dos serviços.** Vários projetos acessam o WMI direto, sem nenhum serviço OEM [S22][S26][S28]. Neste PC os probes (16/11 18:58) foram feitos antes de os serviços serem desativados (17/11 10:00) [S36]; **confirmar que o app continua lendo sensores e perfil com os cinco serviços desativados**.
- **Tecla NitroSense (0xF5)**: chega como tecla comum ao SO [S33][S30]; o app oficial é quem interpreta. Num modelo (ANV16-42) a tecla não chegou por nenhum canal, hipótese de dependência do app Acer [S31] **(NÃO VERIFICADO)**.
- **RGB de 4 zonas por WMI**: não precisa do serviço, nas séries com RGB por WMI [S3][S28]. **RGB por HID (séries S) só era feito pelo PredatorSense no Windows**; nenhum projeto de Windows cobre isso [S21].
- **Flag "app Acer rodando"**: o `facer` precisa ligá-la para o RGB de alguns modelos (`rgb_ec_enable=1`), e o kernel tem `ec_raw_mode` para o caso inverso [S17][S1]. Em Windows, o app Acer faz esse handshake; sem ele alguns recursos podem não responder **(NÃO VERIFICADO por modelo)**.
- **Conflitos**: ventoinha manual e curva conflitam com o PredatorSense, que também escreve nas ventoinhas [S27]; o fancurve avisa para não rodar junto com outro controlador [S22]. Dois escritores = "brigam".
- **Reinstalação**: usuários relatam que o software Acer volta depois do Windows Update (os serviços vêm como componentes de driver no Windows Update) [S34]. **Verificar periodicamente se `StartMode` continua `Disabled`**; não achei o mecanismo exato.

### 6.3 Relatos de BSOD/instabilidade ligados a NitroSense/PredatorSense

Nenhum relato que eu tenha achado traz um bugcheck específico atribuído de forma conclusiva a um serviço desta lista. O que existe (todos anedóticos, vários só via trecho de busca; as páginas da Acer Community retornaram 403 e não foram lidas por inteiro):
- PHN16-72 com Windows 11 24H2: tela azul "ao trocar para Desempenho" no PredatorSense; contornado com Balanced + "Best Power Efficiency" do Windows; sem correção de EC da Acer na data [S34].
- Helios 300: `WHEA_UNCORRECTABLE_ERROR` "sempre com PredatorSense instalado"; desinstalar parou o ciclo [S34]. PH317-56: BSOD parou ao remover drivers Intel DPTF; PredatorSense parecia ser o único jeito de controlar ventoinha [S34].
- PHN16-72: erros de EA anti-cheat/DirectX "com PredatorSense ou serviços Acer ativos" (título de discussão) [S34].
- Post em indonésio: atualização do driver `AcerAirplaneModeController` falha e às vezes dá BSOD (1.0.0.12) [S34].
- Fóruns de suporte (Tom's Hardware) citam `AcerAirplaneModeController.sys` como suspeito em dois BSODs, sem confirmar a causa [S34].
- ANV15-51 com `DPC_WATCHDOG_VIOLATION (0x133)` durante jogos: a resposta no Microsoft Q&A aponta para a GPU/drivers e sugere BIOS e DDU [S34].
- Causas não-Acer frequentes nos mesmos fóruns: microcódigo/BIOS (PHN16-72, i9-14900HX), calor/NVMe (`CLOCK_WATCHDOG_TIMEOUT`) e DPTF [S34].

Neste PC [S36]: bugchecks `0xA` (10/11), `0x7E` (11/11 e 13/11), `0xEF` (12/11 e 17/11 01:38), `0x1C7` (14/11). A variedade (IRQL, exceção de sistema, processo crítico, corrupção de estrutura de dados) é típica de memória/hardware instável, não de um só driver **(inferência; sem análise dos minidumps)**. Recomendo analisar os dumps antes de culpar os serviços.

Implicação para o SenseMonitor: não existe razão documentada para *exigir* a remoção dos serviços Acer, nem para *garantir* que eles são inofensivos. O app deve detectar a presença deles, avisar do conflito de escritores e recusar controlar a ventoinha se detectar PredatorSense/NitroSense ativo.

---

## 7. Tabela proposta de capacidades por família

Legenda de confiança: **A** = medido neste PC ou confirmado por código + hardware de 2+ fontes; **M** = uma fonte em hardware ou código do kernel sem teste; **B** = inferência/relato isolado. Colunas: Sens = temperaturas e RPM por `GetGamingSysInfo`; FanR = leitura de RPM; FanC = modos Auto/Max/Custom + velocidade; Perf = perfis; RGB; Bat = limite de bateria (80%); Tecla.

| Família (DMI) | Sens | FanR | FanC | Perfis (códigos) | RGB | Bat | Tecla |
|---|---|---|---|---|---|---|---|
| **Nitro V15 ANV15-51** (este PC) | CPU, GPU, sistema (A) | 2 ventoinhas (A) | Auto aceito (A); Max/Custom: DAMX diz "testado oficialmente"; o pwm do kernel ainda não tinha teste no relato do patch (M) | Quiet 0, Balanced 1, Perf 4, Eco 6; **sem Turbo** (A, bitmap 0x53) | Provável backlight branco de uma zona (Acer + Linuwu sem four_zone) (M) | Sim (M, allowlist + MODELS.md) | f5 / vk0xFF scan0x75 (A) |
| Nitro V15 ANV15-41 | Igual (M) | (M) | (M) | `nitro_sense=1`: perfis (M, Linuwu) | Sem four_zone (M) | ? | f5 provável (B) |
| Nitro V15 ANV15-52 | (M) | (M) | (M) | **Dependente de BIOS**: WMI falha em V1.53; EC por memória no Linuwu; sem Turbo (M) | Sem four_zone (B) | Sim (1 relato) (B) | f5 (A, hwdb) |
| Nitro V16 ANV16-41/-71, Nitro 16 AN16-41/42/43, Nitro 17 AN17-51 | (M) | (M) | (M) | `nitro_v4`: perfis; ANV16-71 também turbo=1 (M) | 4 zonas (M) | Sim em AN16-43 e ANV16-42 (M) | ANV16-42: nenhum canal conhecido (M) |
| Nitro V16S / 16S AI (ANV16S-41, AN16S-61) | (M) | (M) | (M) | `nitro_v4` (M) | **ENE KB5130 por HID, WMI não acende** (M) | ? | Botão por HID (M) |
| Nitro 5 AN515-58 / AN515-57 / AN517-54 / AN517-41 | AN515-58: kernel mainline (M) | (M) | AN515-58: pwm no kernel (M) | `nitro_v4` (M); AN515-57 sem four_zone | 4 zonas com correção de bytes (AN515-58); AN515-57 teclado vermelho fixo (M) | Sim: 57/58/517-54 (M) | f5 (A) |
| Nitro 5 antigo AN515-43/-44/-45/-54/-55/-56 | AN515-43/-44: sim (M) | (M) | AN515-43: sim, piso ~3950 RPM, sem tabela (M) | **Sem platform_profile** (M); CoolBoost em Auto (M) | 4 zonas (AN515-44/45/55) (M) | AN515-47 não; demais ? | f5 (AN515-44/-47) (M) |
| Helios 300 PH315-52/-53/-54/-55 | CPU, GPU, sistema (M) | 1+1 (M) | Turbo/Custom (M) | **Sem profile (PH315-53 EIO); Turbo = LED + ventoinha + OC 5/7** (M) | 4 zonas (M) | PH315-52: **não** (M) | Botão Turbo = WMI 0x7/0x4 (M) |
| Helios Neo 16 PHN16-71/-72/-73, PH16-71/-72, PH18-71/-73, PHN18-71 | (M) | (M) | PH16-72/PHN16-72 pwm no kernel (M) | Quiet/Bal/Perf/Turbo/Eco (M) | 4 zonas; PHN16-71 per-key "pode falhar"; PHN16-73 estático falha (M) | PHN16-71 sim (M) | PHN16-71 f5 + tecla de modo 0x5 (M) |
| Helios Neo 16S AI (PHN16S-71) | (M) | (M) | (M) | `predator_v4` (M) | **ENE KB5130 por HID** (M) | ? | Botão por HID (M) |
| Triton 14 PT14-51 / PHN14-51 | PHN14: **IDs 0x19..** e RPM por EC (B) | PT14-51: kernel (M) | PT14-51 pwm (M) | PT14-51 turbo+profile (M) | PHN14: 4 zonas (B) | ? | ? |
| Triton 300/500 PT315-5x, PT515/516, PT917, PH517/717 | PT315-52 controle confirmado (M) | (M) | Só PT315-52 confirmado (M) | Turbo "implementado, não testado" (B) | PT315-51: 4 zonas; per-key não suportado (M) | PT315: sim no Triton 300 SE (M) | Botão Turbo (M) |
| Aspire 7 gaming A715-42G e Aspire 5 | Sem GetGamingSysInfo comprovado | Sem comprovação | A515-57: Turbo por WMI funciona, volta em ~2 s (B) | Não sei | Sem | A715-42G sim (M) | ? |

---

## 8. Correções e achados que mudam `firmware-codes.md`

1. **O comando `0x02` não "falhou"**: é o status de bateria/tomada, `ACER_WMID_CMD_GET_PREDATOR_V4_BAT_STATUS` [S1][S9]. A resposta constante `0x0000010000000000` (bit 40) para qualquer `id` é o comportamento esperado de um comando que ignora o `id` (o PC provavelmente estava na tomada) **(inferência; confirmar lendo na bateria)**.
2. **`0x03` (o "sensor extra", 54 °C) é `EXTERNAL_TEMPERATURE_2`** [S1][S22][S27].
3. **Comandos de `GetGamingSysInfo` válidos: 0, 1 e 2.** `0x03..0x05` retornam status 2, como no probe [S36].
4. **Há um bitmap de sensores e um de perfis**; ambos já estão nos nossos dados (`0x227` e `0x53`) e deveriam alimentar o detector em vez de IDs fixos.
5. **Perfil Eco (código 6) existe neste modelo** e o app ainda tratava como "não exposto": o bitmap `0x53` marca Eco como suportado e o Turbo (5) como ausente [S5][S36].
6. **Observação `GetGamingMiscSetting(0x5)` = status 1, `(0x7)` = `0xFF00`:** são os índices de OC; indica "sem OC" neste PC.
7. **Chamada estática vs. por instância** e **leitura de `gmOutput` no PowerShell** são armadilhas documentadas por outros projetos [S23][S26][S27]; já cobertas aqui, mas vale registrar.

---

## 9. Lacunas e próximos passos sugeridos

- **Nenhum projeto documenta como o PredatorSense/NitroSense decide quais recursos mostrar.** O mainline admite não saber ("until the way Acer's OEM software detects available profiles is understood") [S8]. Engenharia reversa do app instalado seria a única forma de saber (fora do escopo: este PC tem os serviços desativados).
- **Confirmar no hardware** (leitura pura, com admin): `BatteryControl.GetBatteryHealthControlStatus(1,1,[0,0])`, `APGeAction.GetFunction(4)`, `GetGamingKBBacklight(1)`, `GetGamingMiscSetting(0x05/0x07)` e `GetGamingLED(1)`, e comparar `GetGamingSysInfo(2)` na tomada vs. na bateria.
- **Validar escrita neste PC** (já previsto na onda 1): perfil Quiet/Performance/Eco e fan Custom, com releitura depois de uma mudança real de estado.
- **Telemetria opt-in**: pedir ao usuário um relatório anônimo com `Model`, BIOS, bitmaps `0x227`/`0x53`-like, status das chamadas acima. É a única forma de crescer a tabela sem hardware.
- **Analisar os minidumps** (`C:\WINDOWS\Minidump`) antes de atribuir os BSODs a software Acer.
- **Assinatura em `APGeEvent`** para o Turbo dos Predator.
- **Tabela de modelos como dados** (JSON versionado), com a heurística "bitmap + tabela + validação do perfil atual" e um modo "somente leitura" para modelos desconhecidos (já planejado em B02/B09).

---

## 10. Fontes

Código-fonte e commits do kernel
- [S1] drivers/platform/x86/acer-wmi.c, master (baixado em 2026-11-17): https://github.com/torvalds/linux/blob/master/drivers/platform/x86/acer-wmi.c
- [S2] Mesmo arquivo nas tags v6.8, v6.12 a v6.19, v7.0, v7.1 (`https://raw.githubusercontent.com/torvalds/linux/<tag>/drivers/platform/x86/acer-wmi.c`)
- [S5] Patch v2 "Add support for Acer Nitro ANV15-51": https://lkml.iu.edu/2607.3/11762.html
- [S6] Resposta de Armin Wolf: https://lkml.iu.edu/2607.3/12660.html
- [S7] Resposta do autor: https://lkml.iu.edu/2607.3/13387.html
- [S8] Commit "Stop using ACPI bitmap for platform profile choices": https://gbmc.googlesource.com/linux/+/b0908e03fdd488a5ffd5b80d86dcfc77207464e7%5E%21
- [S9] Patch PHN16-71 (perfil e tecla de modo): https://patchew.org/linux/20231122065534.3668-1-onenowy@gmail.com/20231122065534.3668-2-onenowy@gmail.com
- [S10] Patch PH315-53 (Turbo): https://lkml.indiana.edu/2108.1/05288.html
- [S11] RFC "Fix setting of fan behavior": https://lkml.rescloud.iu.edu/2502.1/11325.html
- [S12] Patch "Add fan control support": https://patchew.org/linux/20250923215205.326367-1-W._5FArmin@gmx.de/20250923215205.326367-3-W._5FArmin@gmx.de
- [S13] Thread "WMID_gaming_set_fan_mode() behaviour": https://lkml.iu.edu/hypermail/linux/kernel/2501.0/03727.html
- [S14] Patch v7 do driver `acer-wmi-battery`: https://ratatoskr.run/platform-driver-x86/2026/09/17520552/t
- [S15] https://github.com/frederik-h/acer-wmi-battery
- [S16] Lista de modelos: https://raw.githubusercontent.com/frederik-h/acer-wmi-battery/main/MODELS.md

Projetos de comunidade
- [S3] Linuwu-Sense, `src/linuwu_sense.c`: https://github.com/0x7375646F/Linuwu-Sense/blob/main/src/linuwu_sense.c
- [S4] README do Linuwu-Sense: https://github.com/0x7375646F/Linuwu-Sense
- [S17] facer: https://github.com/JafarAkhondali/acer-predator-turbo-and-rgb-keyboard-linux-module
- [S18] DAMX: https://github.com/PXDiv/Div-Acer-Manager-Max e https://raw.githubusercontent.com/PXDiv/Div-Acer-Manager-Max/main/Compatibility.md
- [S19] AcerSense FAQ: https://github.com/kleqing/AcerSense/blob/main/FAQ.md
- [S20] nitrosense-linux: https://github.com/napejoon/nitrosense-linux
- [S21] kbrgb: https://github.com/shwetankg07/kbrgb
- [S22] fancurve: https://github.com/reyi06/fancurve
- [S23] acer-aspire-fan-control: https://github.com/vicentetob/acer-aspire-fan-control
- [S24] rawPSense: https://github.com/joserodpt/rawPSense
- [S25] REA-predatorsense-TOOL: https://github.com/romrepostacks/REA-predatorsense-TOOL
- [S26] nitro-fan: https://github.com/charllysb/nitro-fan
- [S27] predator-control: https://github.com/Flybaby1/predator-control
- [S28] OpenPredatorSense: https://github.com/Xenonesis/Openpredator-Sense
- [S29] NitroSensual: https://github.com/krwclassic/nitrosensual
- [S30] PR #117 (ANV15-52) do Linuwu-Sense: https://github.com/0x7375646F/Linuwu-Sense/pull/117
- [S31] Issue #114 (tecla Nitro V16): https://github.com/0x7375646F/Linuwu-Sense/issues/114
- [S32] Issue #131 (ANV15-52): https://github.com/0x7375646F/Linuwu-Sense/issues/131
- [S37] PRs de quirks (AN515-57, AN515-45, ANV16S-71): https://github.com/BillyAddlers/Linuwu-Sense-AN515-55-Patch/pull/2 , https://github.com/0x7375646F/Linuwu-Sense/pull/92 , https://github.com/0x7375646F/Linuwu-Sense/pull/139 , https://github.com/0x7375646F/Linuwu-Sense/pull/160 (conteúdo lido só por trecho de busca)

Teclas e especificações
- [S33] systemd hwdb: https://github.com/systemd/systemd/blob/main/hwdb.d/60-keyboard.hwdb
- Página oficial Acer do ANV15-51 (backlight branco): https://www.acer.com/ae-en/laptops/nitro/nitro-v-15/pdp/NH.QQEEM.001

Relatos de instabilidade (trechos de busca; as páginas da Acer Community retornaram 403)
- [S34] https://community.acer.com/en/discussion/731596/update-on-predator-helios-neo-16-phn16-72-blue-screen-of-death-keeps-happening-when-updating-to-windows-11-version-24h2-for-x64-based-systems ; https://community.acer.com/en/discussion/599697/my-helios-300-crash-when-i-have-predatorsense-bsod-whea ; https://community.acer.com/en/discussion/735473/ph317-56-instability-issues-bsod-predatorsense-implemented-many-fixes-and-repairs ; https://community.acer.com/en/discussion/731373/acer-predator-helios-neo-16-phn16-72-ea-anticheat-and-directx-errors-when-predatorsense-or-acer-services-enabled ; https://community.acer.com/en/discussion/714188/does-anyone-know-what-kind-of-background-services-are-these-and-can-i-disable-them-in-anv15-51 ; https://community.acer.com/en/discussion/comment/1349595 ; https://learn.microsoft.com/en-us/answers/questions/5633538/constant-bsod-whilst-playing-games-acer-nitro-anv1 ; https://forums.tomshardware.com/threads/bsod-on-gaming-laptop-even-after-fresh-install-and-repair-help.3661713/page-2

Observações locais
- [S36] Este PC (2026-11-17): `Get-CimClass root\WMI`, `Win32_Service`, `Win32_SystemDriver`, `Win32_ComputerSystem`, eventos 1001 do log System, e os arquivos `C:\claude\forja\docs\firmware-codes.md`, `probe-idle.txt`, `2026-11-17-plano-unico.md`.
