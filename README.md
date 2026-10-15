# BatteryWidget

Monitor de bateria para o **mouse Dareu A955** e o **teclado GAMAKAY**, com painel fixo na área de trabalho e aviso de bateria baixa.

Não depende dos softwares oficiais (`OemDrv.exe` da Dareu, `GAMAKAY Driver.exe`). Fala direto com os dongles por HID. Os protocolos foram obtidos por engenharia reversa — a seção [Protocolos](#protocolos) documenta o que foi descoberto e como foi confirmado, que é a parte que você vai querer consultar se algo parar de funcionar depois de uma atualização de firmware.

---

## Como compilar

```cmd
build.cmd
```

Gera `bin\DareuBattery.exe`. Usa o `csc.exe` do .NET Framework 4 que já vem no Windows — sem SDK, sem Visual Studio, sem NuGet. Abrir no Visual Studio também funciona, mas não há `.csproj`: são cinco arquivos `.cs` soltos de propósito, para manutenção rápida.

## Como instalar

```cmd
install.cmd
```

Compila, copia para `%LOCALAPPDATA%\DareuBattery\`, cria a tarefa agendada **"Dareu Battery Monitor"** (logon, sem elevação) e sobe o monitor.

Para remover: `bin\DareuBattery.exe --uninstall` e apagar `%LOCALAPPDATA%\DareuBattery`.

## Linha de comando

| flag | o que faz |
|---|---|
| *(sem flag)* | roda o monitor: ícone na bandeja + painel |
| `--once` | lê os dois dispositivos e imprime; também grava em `last.txt` |
| `--test` | dispara um aviso de exemplo (toast + overlay) |
| `--install` | instala e registra a tarefa de logon |
| `--uninstall` | remove a tarefa agendada |

`--once` é o atalho para diagnosticar: se ele lê certo, o problema está no monitor; se lê errado, está no HID.

---

## Arquitetura

| arquivo | responsabilidade |
|---|---|
| `src/DareuHid.cs` | protocolo do mouse Dareu (VID `0x260D`): envia comando, lê resposta |
| `src/GamakayHid.cs` | leitura do teclado GAMAKAY (VID `0x3151`): só lê feature report, nunca escreve |
| `src/Notifier.cs` | as duas vias de aviso: toast nativo e painel sobreposto |
| `src/Widget.cs` | o painel da área de trabalho (`WidgetForm`) e o `DeviceState` |
| `src/Program.cs` | `Main`, config, ciclo de polling, ícone da bandeja, lógica de alerta |

`tools/` tem o `HidTool.cs` usado na engenharia reversa (`get`, `set`, `listen`, `poll` em qualquer dispositivo HID por VID). Útil se precisar investigar de novo:

```cmd
csc -out:tools\HidTool.exe tools\*.cs
tools\HidTool.exe get  3151 mi_02 00 65        rem lê feature report do teclado
tools\HidTool.exe poll 260d col05 09 17 60 4   rem observa mudanças por 60s a cada 4ms
```

### Ajustar o visual do painel

As medidas estão em constantes no topo de `Widget.cs` (`PAD`, `BAR_W`, `BAR_H`, `BLOCK_TOP`, `BLOCK_H`, `BAR_DY`…). O tamanho da janela é calculado a partir delas, então mexer numa constante redimensiona o painel inteiro sem precisar acertar mais nada. Cores por faixa de carga ficam em `LevelColor`.

---

## Configuração

`%LOCALAPPDATA%\DareuBattery\config.ini`, lido na inicialização e pelo menu **Recarregar configuração**.

| chave | padrão | o que faz |
|---|---|---|
| `IntervalMinutes` | 5 | intervalo entre consultas |
| `Thresholds` | 20,10,5 | níveis que disparam aviso |
| `RepeatMinutes` | 30 | repete o aviso enquanto seguir crítico (0 = não repete) |
| `OverlaySeconds` | 12 | tempo do painel sobreposto na tela |
| `Toast` / `Overlay` | 1 | liga/desliga cada via de aviso |
| `Widget` | 1 | mostra o painel |
| `WidgetTopMost` | 1 | 1 = por cima das janelas; 0 = no nível da área de trabalho |
| `WidgetX` / `WidgetY` | -1 | posição salva (-1 = canto inferior direito) |
| `Keyboard` | 1 | liga a leitura do teclado |
| `KeyboardVid` | 3151 | VID do teclado |
| `KeyboardReportLen` | 65 | tamanho do feature report |
| `KeyboardPercentByte` | 2 | **qual byte do report tem a porcentagem** |

`KeyboardPercentByte` é configurável de propósito: é o parâmetro com maior chance de mudar num firmware novo, e dá para corrigir sem recompilar.

---

## Protocolos

### Mouse Dareu — VID `0x260D` (dongle 2.4G: PID `0x1074`)

Consulta ativa. Duas coleções HID formam o par pergunta/resposta — identificadas pelas *capabilities*, não pelo número da coleção, para funcionar em outros modelos da marca:

- **comando**: feature report de 17 B, usage page `0xFF02`
- **resposta**: input report de 17 B, usage page `0xFF01`

```
comando   08 <cmd> 00 00 00 00 ...zeros...  <chk>
resposta  09 <cmd> <status> <addrHi> <addrLo> <len> <dados...> <chk>
```

Nos dois, **a soma dos 17 bytes mod 256 vale `0x55`**. `status` 0 = ok, 1 = indisponível (mouse dormindo).

> **Pegadinha:** o campo `<len>` vai **zerado na requisição** — quem preenche o comprimento é o dispositivo, na resposta. Mandar `len` preenchido faz o device responder `status=1`, e isso parece "mouse offline".

| cmd | resposta |
|---|---|
| `0x04` | bateria: `len=2`, `dados[0]` = porcentagem |
| `0x03` | **estado do enlace 2.4G**: `len=1`, `1` = ativo no rádio, `0` = fora do rádio |
| `0x08` | leitura de memória (aceita endereço e comprimento reais) |

> **`0x03` não é flag de carga.** Ele vale `0` com o cabo plugado (o mouse troca para modo com fio) **e também** quando o mouse dorme. Tratar `0` como "carregando" gera carga falsa toda vez que o mouse fica parado alguns minutos.
>
> Carga pelo cabo se detecta por **enumeração**: plugado, o mouse aparece com um **PID diferente** do dongle (`0x1097` vs `0x1074`) e some ao desplugar — ver `DareuHid.CablePresent`. Comparar por **PID**, nunca por instância: o mesmo dongle expõe `mi_00` e `mi_01` com tokens de instância distintos, o que dá falso positivo.

### Teclado GAMAKAY — VID `0x3151` PID `0x4011`

Só leitura, nada é escrito. O dongle mantém residente um feature report de 65 B (report id 0) na coleção vendor com usage page `0xFFFF` usage `0x02` (interface `mi_02`):

```
00 <slot> <bateria%> 00 00 01 <online> 01 ...
         └ byte 2
```

> **Pegadinha:** numa máquina recém-ligada, antes de qualquer software da marca tocar o dongle, esse report lê **tudo zero**. É por isso que `GamakayHid.ReadPercent` valida o quadro (`buf[1] <= 1 && buf[5] == 0x01 && buf[2] in 1..100`) em vez de confiar no byte direto.

Confirmado por três fontes independentes: o byte valia `0x2D` (45); o ícone do app oficial estava no bucket `battery_40` (preenchimento medido em pixels: 42,4%, e os assets são 20/39/59/78/100%); e o gRPC do SDK da marca devolvia `Status24{battery:45,isOnline:true}`.

#### Caminho alternativo: gRPC do SDK

Se algum dia o feature report deixar de servir, o SDK oficial é a outra porta. O app GAMAKAY é Electron e **não faz HID**: ele sobe `%APPDATA%\GAMAKAY Driver\iot_driver_v158.exe`, que serve **gRPC em `0.0.0.0:3814`**.

Método: `/driver.DriverGrpc/watchDevList` (request `Empty`, response stream `DeviceList`). Campos de `Device`: `devtype=1 is24=2 path=3 id=4 battery=5 isonline=6 vid=7 pid=8`; dentro vem `danglecommondev.keyboard.status` = `Status24{battery=1, isOnline=2}`.

Para falar com ele sem instalar Node: **`ELECTRON_RUN_AS_NODE=1 "GAMAKAY Driver.exe" script.js`** roda o binário do Electron como Node, com os `node_modules` do app (incluindo `@grpc/grpc-js`) disponíveis. Daí dá para chamar com serializers passthrough (`makeServerStreamRequest`) e decodificar o protobuf na mão.

---

## Avisos de bateria baixa

Duas vias, porque uma só não basta nesta máquina:

1. **Toast nativo** com `scenario="reminder"` (fica fixo até ser dispensado e permanece na Central de Ações). O app se registra em `HKCU\SOFTWARE\Classes\AppUserModelId\Dareu.A955.BatteryMonitor` para ter nome e ícone próprios.
2. **Painel sobreposto** (`Notifier.Overlay`), uma janela `TopMost` + `WS_EX_NOACTIVATE` que aparece **mesmo com o Assistente de Foco suprimindo banners** — que é o que acontece com jogo ou app em tela cheia. Não rouba foco.

O toast sozinho não serve: ele chega na Central de Ações (dá para conferir pelo `LastNotificationAddedTime` no registro) mas o banner não aparece em tela cheia. `scenario="urgent"` é só do Windows 11.

---

## Diagnóstico

- `log.txt` em `%LOCALAPPDATA%\DareuBattery\` registra toda mudança de leitura.
- O asterisco `*` no painel significa **último valor conhecido**, não leitura fresca (mouse dormindo).
- `--once` imprime o estado dos dois na hora.
- Mouse lendo `--`: o mouse dorme quando parado; mexa nele e use **Verificar agora**.
- Teclado lendo `--`: o dongle ainda não publicou status. Abrir o app oficial uma vez popula o report.

## Nota sobre os nomes

O executável, o namespace e a tarefa agendada ainda se chamam `DareuBattery` — vem de quando o projeto só cobria o mouse. Renomear quebraria a tarefa agendada, o AUMID do toast e o mutex de instância única sem ganho funcional. Se quiser renomear mesmo assim, troque nos cinco `.cs`, em `TASK_NAME` e em `Notifier.AUMID`, rode `--uninstall` com a versão antiga e `install.cmd` com a nova.
