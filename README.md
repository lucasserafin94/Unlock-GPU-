# Unlock GPU

Monitor e ajustes para GPUs NVIDIA de notebook, com código próprio e só interfaces públicas.

## O que faz (v3)

- **Painel ao vivo** pela NVML (`nvml.dll`, API oficial da NVIDIA): potência e limite, clock do núcleo, clock da VRAM, nível de energia (P-state), temperatura e uso.
- **Motivo do corte de clock**: limite de energia, temperatura, freio de energia e outros, traduzidos.
- **Gráfico** dos últimos 3 minutos: potência, núcleo e VRAM.
- **Gravação de sessão**: uma leitura por segundo em CSV (`Documentos\TravaVRAM\sessoes`), com resumo sob carga: média, pico, clock médio, temperatura máxima e distribuição da VRAM.
- **Trava da VRAM** no nível máximo (P0) ou intermediário (P2) via `nvidia-smi -lmc` / `-rmc`. Detecta a trava pela própria GPU e opcionalmente trava ao entrar no Windows.

## Limites de escopo

- O caminho padrão usa só APIs públicas (NVML, nvidia-smi); nada de registro do driver, vBIOS ou nvflash.
- Exceção documentada: o kit `app/UnlockGpu.Core/Patch` grava os slots `romOverrideXX` na chave de classe do driver para elevar o teto de potência além da vBIOS. Detalhes em [docs/AUDIT-CONCORRENCIA.md](docs/AUDIT-CONCORRENCIA.md).

## Estrutura

- `src/UnlockGPU.ps1`: app (PowerShell 5.1 + WPF). Modos: janela, `-Lock`, `-Unlock`, `-SelfTest`.
- `src/Nvml.cs`: leitura da GPU pela NVML (C# 5, compilado com `Add-Type`).
- `tests/test-ui-offscreen.ps1`: testa a janela fora da tela com leituras reais, só leitura.

## Roteiro

1. **v3**: monitor, gráfico, gravação, trava da VRAM, offsets (NVML), teto da placa. Nome e logo: Unlock GPU (`assets/`, gerada por `tools/make-logo.ps1`).
2. Limite de potência dentro da faixa do driver (NVML) e perfis.
3. Overclock de núcleo e memória com ajuste automático: sobe em passos, mede, para quando o ganho some ou aparece instabilidade.
4. TGP no máximo permitido por marca (Tongfang/Uniwill primeiro; depois Asus, Lenovo, HP) por interfaces documentadas.
5. Alerta de estabilidade: cruza desligamentos e erros de hardware do Windows com o perfil em uso.
6. Migração para C# compilado (.NET), quando o SDK estiver instalado.
