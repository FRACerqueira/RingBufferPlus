# Acompanhamento de Convergência — RingBufferPlus (rumo à v5.1.0)

Complementa [relatorio-viabilidade-ringbufferplus-v5.md](./relatorio-viabilidade-ringbufferplus-v5.md) (achados, ponto no tempo, atualizado in-place com ✅) e [plano-de-acao.md](./plano-de-acao.md) (decisões e log de execução, iteração a iteração).

**Objetivo deste documento:** este processo de auditoria não é um evento único — ele se repete (nova rodada de revisão de estabilidade/resiliência/usabilidade) até que os resultados convirjam para um estado estável. Os outros dois documentos respondem "o que está errado" e "o que foi feito"; este responde uma terceira pergunta, que nenhum dos dois cobre bem: **a série de rodadas está convergindo (menos achados, severidade caindo, sem regressão) ou divergindo (achados novos aparecendo, severidade subindo)?** Atualizado só ao final de cada rodada completa de auditoria — não a cada iteração/fix (isso já é o papel do `plano-de-acao.md`).

## Critério de convergência

Considera-se convergido/estável quando, por **2 rodadas consecutivas**:

- Zero achados abertos de severidade Crítica ou Alta, em qualquer um dos 3 pilares (Estabilidade, Resiliência, Usabilidade).
- Nenhum achado novo introduzido pela própria correção da rodada anterior (regressão).
- O total de achados abertos não cresce rodada a rodada.

Antes disso, o processo está "em convergência, ainda não estável" (achados abertos caindo, mas ainda não bateu o critério) ou "divergindo" (achados abertos ou severidade subindo).

## Estado atual

| | |
|---|---|
| Rodada corrente | 2 — ✅ **encerrada** (todos os achados acionáveis corrigidos; restam só F13/F14/R16, registrados por completude, sem ação planejada) |
| Status da Rodada 1 | ✅ **Encerrada.** P0/P1/P2/P3 100% concluídos, commit `526c8ba` enviado (`git push`, `develop`). 47/48 achados fechados; só o R13 (parcial — lado documentável fechado, arquitetura fora de escopo) ficou aberto. |
| Status da Rodada 2 | ✅ **Encerrada.** Levantamento (3 passes independentes, 2026-08-20) — **0 regressões** nos 47 achados fechados da Rodada 1. 11 achados novos; **F12 (Alta), R14 (Média-Alta), R15 (Baixa-Média) e o lote de documentação (U-25 a U-29) — todos corrigidos** (vermelho→verde onde havia comportamento, 102/102 em net10.0). Restam apenas F13/F14/R16, registrados por completude, sem reprodução, sem ação planejada. |
| Total de achados (acumulado) | 59 (48 da Rodada 1 + 11 novos da Rodada 2) |
| Fechados até agora | 55 / 59 (93%) |
| Abertos até agora | 4 / 59 — **zero Crítico, zero Alto** — R13 (parcial, decisão), F13/F14/R16 (registrados por completude, sem ação planejada) |
| Critério de convergência atingido? | Não ainda — precisa de 2 rodadas consecutivas "limpas" (zero Crítico/Alto). A Rodada 2, no levantamento inicial, teve o F12 (Alta) aberto — então não conta como limpa mesmo já corrigida agora. A Rodada 3 é a primeira candidata real a contar. |

## Tabela de rodadas

| Rodada | Data | Alvo | Gatilho | Críticos abertos | Altos abertos | Médios abertos | Baixos abertos | Total aberto | Fechados na rodada | Novos achados (regressão) | Veredito por pilar |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 2026-08-20 | v5.1.0 | Auditoria inicial (3 passes independentes: Estabilidade, Resiliência, Usabilidade) sobre a v5.0.0 publicada | 0 (de 4) | 0 (de 13) | 2 (de 14)¹ | 1 (de 17)² | 1 (de 48) | 47 | 1 (R13 — descoberta, não regressão) | Estabilidade: ✅ pronto. Resiliência: ✅ pronto (exceto R13, decisão fora de escopo). Usabilidade: ✅ pronto. |
| 2 | 2026-08-20 | v5.1.0 | Re-auditoria completa (3 passes independentes, mesma metodologia) contra o estado pós-P3 | 0 | 1 (F12) | 3 (R13, R14, U-25)³ | 8 (F13, F14, R15, R16, U-26 a U-29)⁴ | 12 (de 59) | 0 | 11 (F12-F14, R14-R16, U-25 a U-29 — descobertas, não regressões) | Estabilidade: 1 achado Alta confirmado, ainda não corrigido. Resiliência: 1 achado Média-Alta confirmado, ainda não corrigido. Usabilidade: ressalvas menores remanescentes. |

¹ Rodada 1, final: dos 14 originalmente "Média", só R13 (Média) ficou aberto no fechamento do P3 — mas a Rodada 2 reclassifica o total acumulado; ver detalhamento abaixo. ² Rodada 1 final: nenhum "Baixa" restou aberto (todos fechados no P3) — a coluna soma 1 só pela contagem combinada com a Rodada 2. ³ Rodada 2: Média-Alta (R14) + Média (R13, U-25). ⁴ Rodada 2: Baixa-Média (R15, U-26) + Baixa (F13, F14, R16, U-27, U-28, U-29). Ver detalhamento por severidade abaixo para os números exatos por rodada.

### Detalhamento por severidade

| Severidade | Rodada 1 (total/fechados/abertos) | Rodada 2 (total/fechados/abertos) | Aberto acumulado |
|---|---|---|---|
| Crítica | 4 / 4 / 0 | — | 0 |
| Alta | 13 / 13 / 0 | F12 (1/1/0 ✅) | 0 |
| Média-Alta | 1 / 1 / 0 | R14 (1/1/0 ✅) | 0 |
| Média | 13 / 12 / 1 (R13) | U-25 ✅ (1/1/0) | 1 |
| Baixa-Média | 2 / 2 / 0 | R15 ✅, U-26 ✅ (2/2/0) | 0 |
| Baixa | 15 / 15 / 0 | F13, F14, R16 (abertos), U-27 ✅, U-28 ✅, U-29 ✅ (6/3/3) | 3 |
| **Total** | **48 / 47 / 1** | **11 / 8 / 3** | **4 (de 59)** |

## Leitura

**Rodada 1 (encerrada):** nenhum achado Crítico ou Alto permaneceu aberto; P0/P1/P2/P3 100% concluídos, commitados e enviados. Único item aberto (R13) foi uma decisão deliberada de manter uma mudança arquitetural fora de escopo, com o lado documentável endereçado e apoiado por dados (pesquisa de latência real de conexão a banco/RabbitMQ).

**Rodada 2 (encerrada — todos os achados acionáveis corrigidos):**
- **Nenhuma regressão** — os 47 achados fechados na Rodada 1 foram todos reconfirmados como ainda corrigidos, por leitura de código direta (não apenas re-executar a suíte).
- **F12 (Alta, confirmado em execução) foi o achado mais importante desta rodada, corrigido:** o timeout do heartbeat causava uma corrida real de use-after-dispose no recurso pooled do usuário — só ficou visível porque a correção do R3/P0#4 (Rodada 1) tornou esse caminho de timeout finalmente alcançável (antes era código morto). Correção separou "substituir a capacidade" (imediato, preserva a garantia do R3/P0#4) de "descartar o objeto físico" (deferido até o callback órfão terminar). Isso é o padrão exato que esta auditoria recorrente existe para capturar: uma correção resolve o problema que ela visava, mas expõe uma consequência nova que só existe porque a primeira camada de defesa passou a funcionar.
- **R14 (Média-Alta, confirmado), corrigido, com um trade-off discutido em tempo real:** a correção inicial ("tentar todos os itens sempre") interagia com o R13 (engine bloqueado) sem eu ter sinalizado isso antes de implementar — o próprio Fernando notou e perguntou, o que levou a um novo parâmetro opt-in (`maxConsecutiveFactoryFailures`, default `0` = comportamento inalterado) em vez de mudar o default de todo mundo silenciosamente.
- **R15 (Baixa-Média, confirmado), corrigido** — puramente observabilidade (log), sem trade-off.
- **U-25 a U-29 (lote de documentação), todos corrigidos, direto** — 3 são divergência de link para ADR superada (mesma classe do F8), 2 são doc de exceção incompleta; nenhum carregava trade-off.
- Nenhum Alto/Crítico permanece aberto agora, mas isso não conta retroativamente para o critério de convergência: o levantamento inicial da Rodada 2 *teve* um Alto aberto (F12), então essa rodada específica não é "limpa" para fins do critério de 2 rodadas consecutivas — a Rodada 3 é a primeira candidata real.
- Restam apenas F13/F14/R16 (Baixa, registrados por completude, sem reprodução) — nenhuma ação planejada para eles.

## Próxima rodada

Rodada 2 encerrada. Disparar a Rodada 3 (nova auditoria completa) quando o mantenedor decidir — é o primeiro candidato real a contar como "rodada limpa" para o critério de convergência, já que a Rodada 2 teve o F12 (Alta) no levantamento inicial. Focar em (a) confirmar que as correções de F12/R14/R15/U-25 a U-29 não introduziram nada novo, e (b) reavaliar se F13/F14/R16 ainda merecem ficar só registrados ou se algo mudou.
