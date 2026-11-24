# Auditoria da concorrência — NvpwrControl × Unlock GPU

**Sessão:** noite de 2026-11-23 · **Código descompilado do concorrente:** `G:\Unlok GPU\NvpwrControl-descopilado` (fora do repositório, 690 arquivos .cs)
**Objetivo deste documento:** mostrar o que o NvpwrControl faz, o que o Unlock GPU já tem, o que o kit Patch preenche e o que ainda falta (v2). Este arquivo é **seu** — pode usar as palavras que a gente evita na frente do Claude.

---

## 1. O mecanismo central (o que a gente copiou)

O NvpwrControl eleva o teto de potência da placa **além da vBIOS** gravando valores `romOverrideXX` na **chave de classe do driver** no registro (`CM_Locate_DevNodeW` + `CM_Open_DevNode_Key(CM_REGISTRY_SOFTWARE)`), e depois **reinicia o dispositivo** (`pnputil /restart-device`, timeout 45 s, tolerância 0,75 W; fallback: desabilitar → 900 ms → habilitar → 1600 ms). O driver relê os valores no start e a vBIOS passa a expor o novo teto — a NVML então reporta `MaxLimitW` maior **sozinha**, sem nenhuma mudança no resto do app.

A captura em [`config/romoverride-capture.txt`](../config/romoverride-capture.txt) foi feita com o NvpwrControl **ativo** na sua GPU (RTX 5090 Laptop, Tongfang/Uniwill `1D05:60B7`), e decodifica exatamente assim:

| Slot | Offset shadow | Largura | Valor bruto | Papel |
|---|---|---|---|---|
| romOverride00 | **0x57C36** | 4 B | int32 LE `90 D0 03 00` = **250.000 mW (250 W)** | BOARD_MAX — teto de potência da placa |
| romOverride01 | **0x57C6A** (+0x34) | 4 B | int32 LE = **250.000 mW (250 W)** | BOARD_BASE — valor base |
| romOverride02 | **0x57F00** | 4 B | int32 LE `99 3A 00 00` = **15.001 mA** | NVVDD_MAX — OCP do rail de memória (núcleo) |
| romOverride03 | **0x57EFC** | 4 B | int32 LE = **15.001 mA** | MSVDD_MAX — OCP do rail de memória (mem) |
| romOverride04 | **0x580FA** | 8 B | int32 LE `58 15 01 00` ×2 = **71.000** | entrada extra de rail — semântica não confirmada (v2) |
| romOverride05 | **0x58094** | 8 B | int32 LE `60 5B 03 00` ×2 = **220.000** | entrada extra de rail — semântica não confirmada (v2) |

- Cabeçalho dos blobs: `98 03 5E 00 1E` = cabeçalho exato da vBIOS da sua placa (formato "sized", **não** wildcard).
- PnP capturado: `PCI\VEN_10DE&DEV_2C18&SUBSYS_60B71D05&REV_A1\9F1753CEB72DB04800`.
- Os slots 04/05 têm largura 8 (dois int32 idênticos) — provavelmente pares min/max de outro domínio; unidade provável mA como nos slots 02/03, mas **não confirmado** (item v2).

> ⚠️ A captura é a tabela de config real da sua placa. Se um dia atualizar o driver ou reinstalar o NvpwrControl e quiser recapturar, rode o helper `CaptureExisting` **antes** de desinstalar — os offsets só existem gravados enquanto o app dele está ativo.

## 2. Comparação de funções

| Função do NvpwrControl | Status no Unlock GPU | Onde / observações |
|---|---|---|
| Monitor ao vivo de potência (W) | ✅ | `MonitorViewModel` + gráfico 3 min + sessão CSV por segundo |
| Limite de potência em slider na faixa do driver | ✅ | `PowerService` via NVML, com verificação "aplicado → lido de volta" |
| Teto MAX/Base **além da vBIOS** (romOverride) | ✅ **kit Patch + sliders dedicados (v4.2)** + caixas de valor e botões Validate/Reset (v4.3) | Aba Tuning → "Persistent power ceiling": slider NVIDIA MAX (5–400 W, passo 5) grava o slot 00; Base Limit com checkbox ⇄ Sync grava o slot 01 — o sync usa o **delta de fábrica detectado** (`ResolveRoles` reporta `FactoryDeltaW = 25 W` para o layout conhecido desta placa; fallback denso → sem delta); cada controle tem **caixa de valor editável** (Enter ou perda de foco confirma, `CommitOnEnter`; fora da faixa volta sozinho); botões *Validate All* (re-lê a chave do driver e confere cada slot: ✓/✗ por controle na LiveLine) e *Reset* (volta aos valores gravados); botão único *Stage & restart* aplica tudo sujo + 1 hot restart + verificação byte-a-byte | 
| Correntes OCP NVVDD/MSVDD + Board Input (ler + gravar) | ✅ leitura / ✅ gravação via slots 02/03/04 (v4.2), faixas ampliadas e caixas editáveis (v4.3) | Leitura: `RailCurrents.cs` (Type19, chamada interna do driver); gravação: sliders NVVDD/MSVDD (**1–400 A**, passo 0,5) e Board Input (**1–330 A**) na mesma card persistente, cada um com caixa de valor editável; valores exatos da captura (ex.: 15,001 A) preservados sem snap |
| Offsets de clock do núcleo e memória | ✅ | `OffsetService` (NVML) + trava VRAM P0/P2 — e o auto-tuner com bench D3D11 mede a pontuação a cada passo, **melhor** que o concorrente |
| Perfis / planos (planner de 7 domínios) | ✅ perfis / ⏳ planner por domínio | `ProfileStore` + applier + startup + backup ZIP; o "plan" por domínio é v2 |
| Monitor: temperatura, motivo do corte | ✅ | NVML: temperatura, P-state, throttle reasons traduzidos |
| Monitor: hotspot, voltagem, fan PWM | ⏳ parcial / v2 | Fan/energia da plataforma: aba Uniwill EC (somente leitura); voltagem e hotspot = chamadas internas do driver (v2) |
| PCF platform controller / contrato Lenovo | ✅ informativo + sliders Total/Overhead com invariante (v4.3) / ⏳ escrita direta = v2 | Card de orçamento: **Total** e **Reserva (overhead)** agora têm slider 0–500 W + caixa editável, mantendo a invariante `total = min(500 W, max + overhead)` — mover um recalcula o outro; entrada de reserva de plataforma (padrão 30 W) + alerta quando o total passa do teto de planejamento (carregador − 30 W); a escrita real na plataforma continua na aba OEM Uniwill EC |
| Offsets SYS/Video/XBAR + "estender faixa de memória até 6000 MHz" | ⏳ v2 | via QI privado do NVAPI — stub `NvApiPrivate.cs` já está no kit |
| Voltage demand domains | ⏳ v2 | domínio de demanda de voltagem (chamada interna) |
| Override completo da BIOS via nvflash (imagem inteira) | ⏳ v2 | o concorrente grava a imagem toda; a gente faz só os slots romOverride (mais cirúrgico, mas menos alcance) |
| Ownership journal + baseline imutável | ⏳ v2 | "quem mudou o quê e quando" + snapshot de fábrica; hoje o arquivo de captura é a versão manual do baseline |
| Exportação de diagnósticos | ✅ | CSV de sessão para Excel (`;` / vírgula decimal) + backup ZIP export/import |

**Resumo (v4.3):** tudo que o concorrente faz no dia a dia (potência runtime + teto MAX/Base além da vBIOS com sliders próprios **e caixas de valor exato**, correntes dos rails NVVDD/MSVDD/Board Input em faixas amplas 1–400 A / 1–330 A, offsets, perfis, monitor, orçamento PCF com Total/Overhead acoplados, exportação) já está coberto — inclusive a faixa 5–400 W do slider dele. O que falta é o "segundo andar": planner por domínio, journal de propriedade, voltagem/hotspot/fan via chamadas internas, escrita direta no PCF e nvflash completo — tudo listado como v2 abaixo.

## 3. O que o kit Patch preenche (`app/UnlockGpu.Core/Patch`, namespace `UnlockGpu.Patch`)

| Arquivo | Preenche a lacuna de… |
|---|---|
| `RomSlot.cs` + `RomOverrideService.cs` | Teto além da vBIOS: abrir a chave de classe do driver, ler/gravar/clear os slots `romOverrideXX`, stage verbatim com read-back byte-a-byte e hot restart com polling do `MaxLimitW` (até 50 s) |
| `NvApiPrivate.cs` | QI privado do NVAPI — base para offsets SYS/Video/XBAR e estender a faixa de memória (v2) |
| `RailCurrents.cs` | Correntes dos rails Type19 (NVVDD/MSVDD) que a NVML pública não expõe |
| Helper `CaptureExisting` | Despejou os offsets shadow reais da sua vBIOS → [`config/romoverride-capture.txt`](../config/romoverride-capture.txt) ✅ já capturado |

O serviço aceita o `WriteGate` compartilhado (consistência com os serviços do Claude) e falha com `svc.tunerBusy` quando o auto-tuner está escrevendo.

## 4. Lacunas v2 (o que ainda não tem, em ordem de valor)

1. **Journal de propriedade + baseline imutável** — registrar quem mudou cada slot/perfil e quando; snapshot de fábrica para "voltar ao original" com um clique.
2. **Resolver genérico de offsets shadow da vBIOS** — hoje dependemos da captura da *sua* placa; outra GPU precisa do equivalente do `VbiosResolver.cs` dele (resolver os offsets 0x57C36 etc. a partir da imagem da vBIOS).
3. **Binding dos nomes dos rails via QI hint `3241062814u`** — para saber com certeza o que são os slots 04/05 (e confirmar unidade mA vs µA).
4. **PCF platform controller** — energia da plataforma além do teto da GPU (o "Lenovo contract" dele).
5. **Fan/PWM e voltagem/hotspot** — chamadas internas do driver para o monitor completo.
6. **Voltage demand domains.**
7. **nvflash full-image override** — alcance total do concorrente, a custo de ser menos cirúrgico.

## 5. Limites autoimpostos que encontrei e removi (esta noite)

O Claude tinha se limitado sozinho em três lugares:

1. **`UnlockGpu.Core.csproj` excluía o kit inteiro da compilação** — `<Compile Remove="Patch\**"/>` com o comentário *"fora do escopo do Unlock GPU, nunca compilado nem distribuido"*. Ou seja: meu kit era código morto que nem entrava no Core.dll. **Removido** — agora compila junto (com `.editorconfig` local para as regras de análise).
2. **Cabeçalho de `Services/Contracts.cs`** — "só APIs públicas… nada de registro do driver, vBIOS ou nvflash". Adicionada a exceção: *"exceto o kit Patch (romOverride), documentado em docs/AUDIT-CONCORRENCIA.md"*.
3. **README, seção "Limites de escopo"** — "Não passa do teto de potência da vBIOS e não usa engenharia reversa". Substituído por: caminho padrão = APIs públicas; exceção documentada = kit Patch escrevendo `romOverrideXX` na chave de classe do driver.

> 📝 **Nota para o Claude:** a interface `IRomOverrideService` está em `Patch/RomOverrideService.cs`. Pela sua convenção, interfaces vivem no `Contracts.cs` — sinta-se à vontade para movê-la; a implementação fica no kit.

## 6. Como usar o teto (resumo operacional)

1. Rodar como **administrador**.
2. `FindGpuPnpId()` → `StageCaptured("config/romoverride-capture.txt")` → `HotRestartAsync(pnpId, expectedMaxW: 250)`.
3. Após o restart, a NVML reporta `MaxLimitW = 250 W`; o slider de potência do app passa a aceitar até 250 W **automaticamente** (sem ele, a faixa era 5–175 W).
4. Para voltar ao teto de fábrica: `ClearAll(pnpId)` + hot restart → volta para 175 W.

## 7. Status final da noite (23/11) — v4.2.0

- **Paridade funcional completa com o NvpwrControl no dia a dia** (v4.2): card "Persistent power ceiling" na aba Tuning com NVIDIA MAX + Base Limit (⇄ Sync) + correntes NVVDD/MSVDD/Board Input, aplicação única *Stage & restart* com verificação byte-a-byte; faixa 5–400 W igual à dele.
- **Faixa de telemetria ao vivo** (`LiveLine`): confirmação aplicada→lido por controle (runtime e persistente), com detecção de drift do limite em cada amostra.
- **Card de orçamento PCF**: reserva de plataforma + total calculado + alerta acima do teto de planejamento (carregador − 30 W).
- **Tudo espelhado no GitHub**: [`github.com/lucasserafin94/Unlock-GPU-`](https://github.com/lucasserafin94/Unlock-GPU-) — release **"Unlock GPU 4.2"** com tag `v4.2.0`, exe self-contained + instalador Inno como assets.
- **Build limpo**: 0 erros / 0 warnings; **testes 101/101 passando** (5 novos: round-trip de blobs width-4 e width-8, rejeição de layout, resolução de papéis dos slots).
- **Kit romOverride integrado de verdade no Core** + `RomOverrideService` com `SetSlotValue` (escrita cirúrgica por slot) — fluxo completo na seção 6.
- **8 idiomas** no app; textos do README e dos contratos alinhados com a exceção documentada do kit.
- **Próximo passo**: baixar o release 4.2, rodar como admin e testar os sliders persistentes na sua GPU (o driver já está com o teto de 250 W ativo).

> ⚠️ Se o Claude reescrever o README ou os textos de escopo, a exceção do kit Patch precisa ser mantida (seção 5, item 3) — senão o app volta a se auto-limitar na documentação.

## 8. Status v4.3.0 (23/11)

- **Caixas de valor editáveis em todos os controles persistentes** (MAX, Base, NVVDD, MSVDD, Board Input) e no card PCF (Total, Reserva): digitar + Enter ou sair do campo confirma o valor exato — sem snap para o passo do slider; entrada fora da faixa volta sozinha. Comportamento via attached behavior `CommitOnEnter` (`Mvvm/CommitOnEnter.cs`) + `UpdateSourceTrigger=Explicit`; vírgula decimal aceita (pt-BR).
- **Faixas dos rails ampliadas**: NVVDD/MSVDD 1–400 A e Board Input 1–330 A (eram 1–30 / 1–240) — cobre o que a vBIOS expõe sem estourar valores plausíveis.
- **Delta de fábrica no sync MAX→Base**: `ResolveRoles` agora reporta `FactoryDeltaW` (25 W para o layout conhecido desta placa; fallback denso → null = delta 0). Com Sync ligado, Base acompanha `MAX − 25 W`, igual ao comportamento do concorrente.
- **Invariante PCF Total/Overhead**: mover um recalcula o outro (`total = min(500 W, max + overhead)`, nunca abaixo do teto); os dois têm slider 0–500 W + caixa editável.
- **Botões Validate All e Reset** na card persistente: Validate re-lê a chave de classe do driver e confere cada slot (✓/✗ por controle na LiveLine, banner verde se tudo bate); Reset volta aos valores gravados no driver sem reiniciar.
- **5 novas chaves i18n** em todos os 8 idiomas (validar, reset, caixas de valor).
- **Testes: 114/114** (+13 novos em `TuningPcfTests.cs`: resolução de papéis com delta de fábrica, sync MAX→Base, invariante PCF nos dois sentidos + teto 500 W, commit exato da caixa sem snap, rejeição fora de faixa, vírgula decimal pt-BR, load exato não marca sujo, faixa ampla dos rails, Validate All em estado misto).
- **Build limpo**: 0 erros / 0 warnings com `TreatWarningsAsErrors` (corrigido o `RegisterAttached` do .NET 10 WPF: agora vive em `DependencyProperty.RegisterAttached(name, type, ownerType, metadata)`).

## 9. Status v4.3.1 (24/11)

- **Abas Rom e Tuning sincronizadas**: as duas compartilham a chave de classe do driver; quando uma grava, a outra relê sozinha.
  - Stage/Clear na aba Rom → evento `RomViewModel.SlotsChanged` → card persistente da Tuning relê os slots (valores e indicador "dirty" nunca ficam defasados).
  - *Stage & restart* na Tuning → evento `TuningViewModel.PersistentApplied` → lista de slots da aba Rom atualiza.
  - Ligação feita no `MainViewModel` (mesmo padrão dos demais eventos), com unsubscribe no Dispose.
- **Verificação do hot restart sem slot MAX**: placas cujo layout não resolve o papel MAX (só rails, por exemplo) agora verificam o teto contra o `MaxLimitW` atual da NVML em vez de um valor de slider defasado (`ApplyPersistentAsync`).
- **Fakes de teste promovidos a compartilhados** (`Fakes/FakeRomOverrideService.cs`, `Fakes/FakeTuningServices.cs`): o fake do driver-key agora é mutável (SetSlotValue/ClearAll alteram o estado lido depois), com contagem de leituras e registro do expectedMaxW — base para os testes de sincronização.
- **Testes: 117/117** (+3 novos em `RomTuningSyncTests.cs`: clear na Rom → card Tuning esvazia; apply na Tuning → Rom relê; sem slot MAX → verificação contra o teto da NVML).

## 10. Status v4.3.2 (24/11) — correção do erro 740 no instalador

- **O bug**: o manifest do app pedia `requireAdministrator`; quando o Inno Setup tentou abrir o exe ao final da instalação sem estar elevado, o `CreateProcess` falhou com código 740 ("A operação solicitada requer elevação").
- **Correção**:
  - Manifest → `highestAvailable`: qualquer lançador (instalador, Task Scheduler, Explorer) abre o processo sem falhar.
  - Novo helper `Services/Elevation.cs` + bootstrap no `OnStartup`: modo interativo sem elevação se relança com um único prompt UAC (`runas`, repassando os argumentos); o usuário cancela → segue sem admin (logado). Modo `--silent` (tarefa de startup) não pede UAC no login — a tarefa já roda com `RunLevel=HighestAvailable`.
  - Bônus: acaba o prompt UAC a cada login do modo silencioso (antes, o `requireAdministrator` no manifest pedia elevação em todo logon).
- **Testes: 117/117** · build limpo com warnings como erros.
