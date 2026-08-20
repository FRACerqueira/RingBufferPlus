# RingBufferPlus v5.0.0 — Relatório de Viabilidade de Produto

**Escopo:** estabilidade, resiliência e usabilidade do release v5.0.0
**Método:** três passes de auditoria independentes (sem contexto compartilhado entre si), cada um verificado em código em execução, não apenas por leitura de código
**Estado do repositório:** inalterado — toda a verificação rodou a partir de probes descartáveis fora do repositório, referenciando a biblioteca compilada
**Veredito geral: SEGURAR O LANÇAMENTO.** Não promover a v5.0.0 como pronta para produção até que os bloqueadores da [§3](#3-plano-de-ação-recomendado) sejam corrigidos e reverificados.

**Status de execução (2026-08-20):** **os 6 bloqueadores P0 (§3) estão todos corrigidos e verificados** — F1, F2, F3, F4, F5, F10, R1, R2, R3, R12, U-01 e U-02 fechados com protocolo vermelho/verde (ou, quando a corrida não reproduziu mesmo com esforço redobrado, com o mecanismo nomeado explicitamente per regra 5 do CLAUDE.md). Suíte completa: 85/85 testes, 0 warnings, net8.0/net9.0/net10.0. Acompanhar o progresso item-a-item em [`plano-de-acao.md`](./plano-de-acao.md). Ainda pendentes: os itens P1 (§3) e as 3 decisões escaladas em [§4](#4-decisões-que-precisam-de-você-não-de-um-ajuste-de-texto) — o veredito "segurar o lançamento" acima referia-se especificamente aos bloqueadores P0, que não são mais o impedimento.

---

## Sumário

1. [Sumário executivo](#1-sumário-executivo)
2. [O problema central](#2-o-problema-central-uma-causa-raiz-duas-confirmações-independentes)
3. [Plano de ação recomendado](#3-plano-de-ação-recomendado)
4. [Decisões que precisam de você, não de um ajuste de texto](#4-decisões-que-precisam-de-você-não-de-um-ajuste-de-texto)
5. [Achados detalhados — Estabilidade](#5-achados-detalhados--estabilidade-concorrência-e-correção)
6. [Achados detalhados — Resiliência](#6-achados-detalhados--resiliência-comportamento-sob-falha)
7. [Achados detalhados — Usabilidade](#7-achados-detalhados--usabilidade-superfície-de-api-e-documentação)
8. [Metodologia](#8-metodologia)
9. [Veredito final](#9-veredito-final)

---

## 1. Sumário executivo

| Eixo | Veredito | Manchete |
|---|---|---|
| **Estabilidade** (concorrência, correção) | **NÃO PRONTO** | Uma factory que lança exceção mata o engine permanentemente; todo método público assíncrono (`Warmup`/`Acquire`/`Switch`/`Dispose`) pode então travar para sempre. Confirmado em execução, **inclusive em capacidade fixa**, não só elástica. |
| **Resiliência** (comportamento sob falha) | **NÃO PRONTO** | Mesmo defeito, alcançado pelo caminho realista: RabbitMQ, banco de dados ou qualquer factory de recurso externo real falha *lançando exceção*, não por cancelamento — que é o único modo de falha para o qual o engine foi construído para sobreviver. |
| **Usabilidade** (API e documentação) | **PRONTO COM RESSALVAS** | A profundidade da documentação é genuinamente boa (10 guias, ADRs, referência gerada, zero links quebrados). Mas as duas coisas que um novo adotante toca primeiro — o Quickstart do README e a instrução principal do guia de DI — falham ao copiar e colar. Correções são só texto, sem risco de código, cerca de um dia de trabalho. |

**Por que isso importa comercialmente:** os defeitos se concentram exatamente nas funcionalidades que diferenciam esta biblioteca de um pool de objetos comum — autoscaling elástico e pooling de recursos externos (canais RabbitMQ, conexões de banco). Eles se manifestam como **comportamento silenciosamente errado**, não como exceções: um engine morto, um contador de capacidade que mente, um scale-up que silenciosamente nunca acontece, métricas que reportam sucesso para operações que falharam. "Silenciosamente errado" é o modo de falha mais caro de diagnosticar em produção, e hoje não há nenhum teste que o proteja — a suíte de testes não tem, em lugar nenhum, um teste com uma factory que lança exceção.

**A parte tranquilizadora:** a arquitetura em si é sólida. Os dois passes técnicos confirmaram, de forma independente, que a aposta arquitetural central do rewrite v5 (um único consumidor em canal (channel) que possui todo o estado de escala) realmente funciona — nenhuma atualização perdida, nenhum estado corrompido, nenhum deadlock encontrado na lógica de escala em si. O que falta é um perímetro defensivo ao redor desse engine, e a correção é pequena, local e não exige redesenhar nada.

---

## 2. O problema central: uma causa raiz, duas confirmações independentes

O passe de Estabilidade e o passe de Resiliência trabalharam a partir de arquivos diferentes, testes diferentes e modelos mentais diferentes — e convergiram, de forma independente, para a mesma linha de código.

> **Todo caminho de erro da factory até o loop de comandos do engine filtra exclusivamente por `OperationCanceledException`.**
> `RingBufferManager.cs:513,516,528,363,370,574,577` — todo `catch` na cadeia entre `Factory(...)` e o `await foreach` do engine é `catch (OperationCanceledException)`. Nada além disso é capturado em nenhum ponto desse caminho.

Factories reais não falham só por cancelamento. Uma factory de conexão de banco de dados lança `SocketException`; o RabbitMQ.Client lança `BrokerUnreachableException`, `AlreadyClosedException`, `OperationInterruptedException`; código de usuário lança `NullReferenceException`, `InvalidOperationException`. Qualquer uma dessas escapa de todos os catches, derruba (faults) o `_engineTask`, e a única thread dona de todo o estado de escala desaparece pelo resto da vida da instância — sem exceção, sem log, sem métrica marcando o momento em que isso aconteceu.

**Consequências confirmadas, reproduzidas em execução, em um buffer `FixedCapacity(3)` simples (ou seja, isto não é um defeito exclusivo de elástico/autoscale):**

| Gatilho | Comportamento observado |
|---|---|
| Factory lança exceção durante `BuildWarmupAsync` | `WarmupAsync()` trava para sempre — nenhum timeout se aplica aqui |
| ...depois `AcquireAsync()` | trava para sempre |
| ...depois `DisposeAsync()` | trava para sempre — **o shutdown do host nunca completa** |
| Factory lança exceção *depois* de um warmup saudável | `SwitchToAsync()` trava para sempre; `DisposeAsync()` em vez disso **lança a exceção antiga da factory**, abortando a limpeza antes de drenar os itens do pool |
| Uma chamada de `Invalidate()` durante a instabilidade | `CurrentCapacity` continua reportando o número antigo, enquanto o pool está de fato com um item a menos — permanentemente, mesmo depois que a factory se recupera |
| Autoscale-on-fault habilitado | depois que o engine morre, novos timeouts de acquire produzem **zero** scale-up — o autoscale é silenciosa e permanentemente desabilitado |

**Duas leituras, ambas corretas, ambas necessárias:**
- O passe de **Estabilidade** enquadra isso como uma ausência de fronteira de exceção (exception boundary) no próprio engine de concorrência — o defeito é que a máquina de estados não tem caminho de falha para seus próprios comandos, e é isso que transforma "a factory lançou uma exceção" em "toda API pública trava para sempre", incluindo `Warmup`, `Switch` e `Dispose`, nenhum dos quais tem a ver diretamente com a factory.
- O passe de **Resiliência** enquadra isso pelo lado do gatilho — uma indisponibilidade de broker/banco, que é a condição operacional normal que esta biblioteca promete suportar (ver `doc/guides/usage-rabbitmq.md`, que promete explicitamente degradação graciosa em caso de queda de conexão). Essa promessa não se sustenta hoje.

Corrigir a fronteira de exceção ausente uma única vez, no loop do engine, resolve as duas leituras simultaneamente — ver Ação 1 abaixo.

---

## 3. Plano de ação recomendado

Ordenado pelo que precisa acontecer antes da próxima afirmação pública de "pronto para produção".

### P0 — Bloqueadores de release

| # | Correção | Fecha | Por que é P0 |
|---|---|---|---|
| 1 ✅ | **[CORRIGIDO]** Capturar `Exception` (não só `OperationCanceledException`) em `ProcessCommandAsync`, `RunEngineAsync`, `CreateSingleReplacementAsync`. No catch: logar, falhar os `TaskCompletionSource`s de `Accepted`/`Completion` do comando, descartar itens parcialmente criados, e **manter o loop rodando**. | Estabilidade F1, Resiliência R1 | O defeito de uma linha, 100% reproduzível, descrito na §2. Tudo o que decorre dele (travamentos, perda silenciosa de capacidade, autoscale morto) desaparece assim que o loop sobrevive a uma exceção lançada. |
| 2 ✅ | **[CORRIGIDO — apenas a parte de token; ver [plano-de-acao.md](./plano-de-acao.md) P0#2]** Fazer todo `await` sobre um `TaskCompletionSource` do engine (`WarmupCoreAsync`, `SwitchToAsync`) carregar `_lifetime.Token`; ~~ao sair do engine, drenar `_commands` e falhar o TCS de cada comando restante em vez de descartá-los silenciosamente~~ (decidido como não necessário — ver plano de ação). | Estabilidade F4, Resiliência R1/R2 | Sem isso, uma exceção de factory ou um shutdown cancelado ainda deixam waiters individuais (não só chamadas futuras) travados mesmo após a correção 1. |
| 3 ✅ | **[CORRIGIDO]** Substituir as guardas não atômicas `bool _disposed` por `Interlocked.Exchange` tanto em `RingBufferValue.DisposeAsync` (`RingBufferValue.cs:63-71`) quanto em `RingBufferManager.DisposeAsync` (`RingBufferManager.cs:240-241`). | Estabilidade F2, F10 | F2 está **confirmado em execução**: double-dispose concorrente entrega a *mesma instância do pool* para duas chamadas simultaneamente (~3,8% de incidência em um probe de 2000 iterações). Para uma conexão de banco ou canal pooled, isso é uso intercalado por dois chamadores — um bug de integridade de dados, não um vazamento. |
| 4 ✅ | **[CORRIGIDO]** Aplicar o timeout do pulso do heartbeat com `Task.WhenAny(work, Task.Delay(PulseHeartBeat))` (ou `.WaitAsync(token)`) em vez de passar um `CancellationToken` para `Task.Run`, que não cancela de fato um delegate em execução. | ~~Estabilidade F3,~~ Resiliência R3 | **Confirmado em execução**: um callback de heartbeat que bloqueia para a bomba de heartbeat para sempre e prende um item do pool — exatamente o modo de falha que um heartbeat existe para detectar, não detectado porque a guarda feita para isso é código inalcançável. **Nota de correção (2026-08-20): esta linha, na síntese original, citava "Estabilidade F3" por engano — F3 é um bug diferente (ver linha 5 abaixo) que esta ação não fecha. Removido daqui; F3 será fechado pela Ação 5.** |
| 5 ✅ | **[CORRIGIDO — também fecha F3, realocado do item #4]** Tornar a limpeza do `DisposeAsync` incondicional: alargar o catch do `Task.WhenAll(pending)` para `Exception`, e mover a drenagem de itens + o dispose de `_lifetime`/`_meter`/`_activitySource` para um `finally`. Também descartar o item no handler de `ChannelClosedException` engolido em `RingBufferManager.cs:345`, em vez de perdê-lo. | Estabilidade F3, F5, Resiliência R2/R12 | Confirmado: hoje, `DisposeAsync` pode lançar uma exceção antiga **antes** de drenar os itens do pool, vazando cada um deles mais o `Meter`/`ActivitySource` do próprio buffer. |
| 6 ✅ | **[CORRIGIDO]** Corrigir o Quickstart do `README.md` (adicionar `CancellationToken cancellation = default;`) e remover, do guia de DI, a instrução de injetar `IRingBufferManualScaleService<T>` diretamente (`doc/guides/usage-dependency-injection.md:46,54` — esse tipo nunca é registrado e a resolução lança exceção). | Usabilidade U-01, U-02 | Ambos são a primeira coisa que um novo adotante copia. Correções puramente de texto, risco de código zero. |

**Antes de reafirmar "pronto para produção", adicionar testes de regressão para:** uma factory que lança uma `Exception` comum (nenhum existe hoje — busquei em toda a suíte), double-dispose concorrente de um lease, dispose durante um warmup em andamento com timeout de teste rígido (o teste de contrato `RingBufferContractTests.cs:275` já cobre esse cenário, mas hoje passa só porque o engine geralmente vence uma corrida — é um **travamento latente de CI**, não uma garantia verificada), e dispose durante heartbeat ativo.

### P1 — Mesmo release ou follow-up imediato (sem mudança de comportamento visível ao usuário, ou uma mudança claramente contida)

| # | Correção | Fecha |
|---|---|---|
| 7 | Marcar `scale.operations`/`scale.duration` com um resultado (sucesso/falhou/expirou), e definir `ActivityStatusCode.Error` no span de scale quando uma tentativa falhar ou expirar. | Resiliência R8 |
| 8 | Desacoplar o prazo do scale-up da janela de amostragem (`SamplesBase`), ou documentar/validar que ele precisa exceder `delta × FactoryTimeout`; manter itens parcialmente criados em um timeout parcial em vez de descartar todos; redimensionar `samples/RingBufferPlusRabbitSample` (sua própria configuração torna o scale-up estruturalmente impossível acima de ~500ms por chamada de factory). | Resiliência R5 |
| 9 | Corrigir a fórmula do alvo de scale-up para que o autoscale não fique permanentemente desabilitado quando `initialCapacity == minCapacity` (uma configuração legal e natural). | Resiliência R4 |
| 10 | Adicionar ao `CHANGELOG.md`, na seção "Breaking changes v5.0.0", uma tabela de nomes de interface antigo→novo, além de uma nota de que `SwitchToAsync` não existe mais em `IRingBufferService<T>`. README e ADR004 chamam essa seção de *única* referência de migração — hoje ela omite as duas quebras com maior chance de atingir código real de usuários v4. | Usabilidade U-08 |
| 11 | Corrigir seis afirmações de documentação erradas/ambíguas: a condição do alvo de scale-up (U-03), a doc de retorno do `SwitchToAsync` (ainda descreve o v4, U-04), a afirmação de "retorna imediatamente" que na verdade pode bloquear por ~30s (U-05), o comentário do sample RabbitMQ ensinando a semântica v4 de `LockWhenScaling` para um cenário que hoje é um no-op (U-06), o off-by-one do limiar de falhas do autoscale repetido em quatro lugares (U-07), o tipo de exceção errado no guia de autoscale (U-21). | Usabilidade U-03–U-07, U-21 |

### P2 — Escalado para você (ver §4 para a análise completa de trade-off — não resolver como ajuste de doc)

- `LockWhenScaling()` é um método público em `IRingBufferAutoScaleBuilder<T>` que é um no-op documentado — e já enganou o autor do próprio sample RabbitMQ deste repositório.
- Um `WarmupAsync` que falha destrói permanentemente a instância (exceção cacheada via `Lazy<Task>`), enquanto todo guia de DI recomenda registrar o buffer como singleton.
- A comparação de contagem de falhas do autoscale (`>` vs. o "após a primeira falha" documentado) — corrigir o código para bater com quatro locais de documentação, ou corrigir os quatro locais de documentação para bater com o código.

### P3 — Passe de documentação/higiene de acompanhamento (sem urgência de release)

Itens Médios/Baixos restantes: Estabilidade F6, F7, F9, F11; Resiliência R6, R9, R10, R11; Usabilidade U-09 a U-24 (exceto os já listados acima). Ver §5–§7 para a lista completa; nenhum desses bloqueia um release sozinho.

---

## 4. Decisões que precisam de você, não de um ajuste de texto

Seguindo a regra padrão para este tipo de revisão: um achado cuja correção carrega um trade-off real é escalado com opções, não resolvido silenciosamente. Três se qualificam.

### 4.1 — `LockWhenScaling()` no builder de autoscale: remover, ou manter como no-op documentado?

`IRingBufferAutoScaleBuilder.cs:65-73` afirma que o método é "aceito mas não tem efeito observável. Mantido aqui apenas para que a cadeia fluente continue compilando sem alteração." O ADR007 se propôs explicitamente a eliminar exatamente esse tipo de armadilha — "interações silenciosas em runtime", "um `false` silencioso em runtime" (ADR007 §28, §38, §54) — e até esboça `LockWhenScaling` como algo que deveria existir só onde faz sentido (§55). Concretamente, isso já enganou alguém: `samples/RingBufferPlusRabbitSample/Program.cs:87-94` chama `.LockWhenScaling()` imediatamente antes de `.AutoScaleAcquireFault()`, com um comentário afirmando que a execução demonstra o comportamento de lock — não demonstra nada, porque a configuração é inerte nessa posição (ver U-06/U-10 para detalhes).

- **Opção A — remover de `IRingBufferAutoScaleBuilder<T>`.** Transforma o erro em um erro de compilação no ponto de chamada, que é o sinal em tempo de compilação que o ADR007 foi escrito para garantir. **Custo:** uma mudança quebradora (breaking change) na superfície fluente, logo após um release já justificado por "breaking changes autorizadas" (ADR006) — o timing pode ou não ser o certo.
- **Opção B — manter, mas dificultar cair na armadilha:** desencorajamento no estilo `[Obsolete]` mais uma nota de revisão no ADR007 registrando o desvio. **Custo:** a superfície pública mantém um método cuja única propriedade documentada é não fazer nada.

### 4.2 — É aceitável que "falha no warmup destrói permanentemente a instância", dado que todo guia manda registrar como singleton no DI?

`RingBufferManager.cs:136` envolve o warmup em `Lazy<Task>`, que cacheia a exceção — confirmado intencional pelo próprio comentário do código ("uma instância cujo warmup lança exceção está permanentemente quebrada por design; construa uma nova instância para tentar de novo"). Nada na documentação XML pública de `WarmupAsync` ou `BuildWarmupAsync` diz isso, e tanto `doc/guides/usage-dependency-injection.md` quanto `concepts.md` empurram o registro como singleton. Consequência prática: uma falha transitória na inicialização do host (um banco de dados ainda não aceitando conexões, um broker ainda subindo) desabilita permanentemente o pool pelo resto da vida do processo, recuperável só com um restart completo.

- **Opção A — manter o comportamento, documentá-lo claramente** em `WarmupAsync`/`BuildWarmupAsync` e no guia de DI, deixando operadores decidirem se isso é aceitável para suas garantias de ordem de inicialização.
- **Opção B — adicionar uma forma de tentar de novo** (por exemplo, um caminho de re-warmup, ou orientação para registrar um delegate de factory que já tenta novamente com backoff antes de entregar o controle ao `RingBufferManager`).

Isso é uma questão de comportamento, não de documentação — o ajuste de doc (deixar claro o comportamento atual) deveria acontecer independentemente de qual caminho for escolhido.

### 4.3 — Limiar de falhas do autoscale: corrigir o código, ou corrigir quatro locais de documentação?

`RingBufferManager.cs:400`: `if (_faultCount > NumberFault && ...)`. Com o padrão `numberOfFaults: 1`, o scale-up de fato dispara na **segunda** falha, não na primeira. Quatro locais diferentes documentam "após a primeira falha" ou uma fórmula de tempo de reação que assume isso (`IRingBufferElasticBuilder.cs:98`, a referência gerada, `doc/guides/usage-elastic-autoscale.md`, e o `CHANGELOG.md` — o documento posicionado como única referência de migração v4→v5). O próprio `samples/RingBufferPlusBasicTriggerScale` deste repositório passa `AutoScaleAcquireFault(0)` especificamente para obter "dispara na primeira falha" — forte evidência de que a intenção documentada era `>=`, não `>`.

- **Opção A — mudar a linha 400 para `>=`.** Bate com toda afirmação documentada. **Custo:** uma mudança de comportamento no timing do autoscale para quem hoje depende do off-by-one como está (improvável, mas não zero).
- **Opção B — corrigir os quatro locais de documentação** para declarar a regra real ("dispara na falha de número `numberOfFaults + 1`; passe `0` para primeira-falha"). **Custo:** nenhum além do ajuste de texto, mas a semântica contraintuitiva de "`0` significa primeira falha" permanece.

---

## 5. Achados detalhados — Estabilidade (concorrência e correção)

**Veredito do passe: NÃO PRONTO.** A arquitetura de concorrência (o canal de consumidor único do ADR001) é sólida — nenhuma atualização perdida, nenhum estado corrompido, nenhum deadlock encontrado na lógica de escala propriamente dita. O que falta é o perímetro defensivo: uma fronteira de exceção ao redor do loop do engine, guardas de dispose atômicas, e um timeout de heartbeat que seja de fato aplicado. Todas são correções pequenas, locais e testáveis de forma independente.

| ID | Severidade | Achado | Localização | Recomendação |
|---|---|---|---|---|
| **F1** | **Crítica — ✅ CORRIGIDO** | Nenhuma fronteira de exceção no loop do engine; qualquer exceção que não seja de cancelamento vinda da factory mata permanentemente o `_engineTask`, órfão de todo waiter atual e futuro. Ver [§2](#2-o-problema-central-uma-causa-raiz-duas-confirmações-independentes). | `RingBufferManager.cs:513,516,528,363,370,382` | Capturar `Exception`, falhar os waiters, manter o loop vivo (Ação 1). **Corrigido — ver [plano-de-acao.md](./plano-de-acao.md) P0#1.** |
| **F2** | **Alta — confirmada em execução — ✅ CORRIGIDO** | A guarda de `RingBufferValue.DisposeAsync` é um `bool` não atômico; double-dispose concorrente duplica um lease. Probe de 2000 iterações: 76 leases duplicados (~3,8%), incluindo um caso confirmado de dois chamadores simultâneos segurando instâncias idênticas por `ReferenceEquals`. | `RingBufferValue.cs:24,63-71` | `Interlocked.Exchange` em uma flag `int` (Ação 3). **Corrigido — ver [plano-de-acao.md](./plano-de-acao.md) P0#3.** |
| **F3** | **Alta — confirmada em execução — ✅ CORRIGIDO** | `DisposeAsync` lança `ObjectDisposedException` quando um pulso de heartbeat está em andamento (~18% de incidência com pulso de 15ms em um probe de 400 iterações), abortando a limpeza antes de drenar os itens do pool. **Não é o mesmo bug que R3** (callback bloqueante — já corrigido); F3 é a corrida entre `_disposed` virar `true` e o `AcquireAsync` interno do heartbeat rodar. | `RingBufferManager.cs:151,615,640-643,270-275` | Capturar `ObjectDisposedException` no caminho do heartbeat; tornar a limpeza do `DisposeAsync` incondicional (Ação 5). **Corrigido — ver [plano-de-acao.md](./plano-de-acao.md) P0#5.** |
| **F4** | **Alta — confirmada em execução — ✅ CORRIGIDO** | A saída do engine por cancelamento descarta todos os comandos na fila sem falhar seus `TaskCompletionSource`s. `WarmupCoreAsync` aguarda um deles sem token — um `DisposeAsync` chamado sobre uma instância ainda não aquecida pode travar para sempre. O teste de contrato `RingBufferContractTests.cs:275` cobre esse cenário, mas passa só por vencer uma corrida; reproduziu como travamento na 2ª tentativa nesta auditoria. | `RingBufferManager.cs:243,244,255,303-304,357,370-373` | Token em todo await de TCS; drenar-e-falhar na saída do engine (Ação 2). **Corrigido (token nos awaits — ver [plano-de-acao.md](./plano-de-acao.md) P0#2, que também registra uma correção de entendimento sobre a mecânica exata dessa corrida).** |
| **F5** | **Média-Alta — ✅ CORRIGIDO** | Itens retirados no momento do shutdown nunca são descartados — o handler de `ChannelClosedException` do `TurnbackAsync` engole o retorno com um comentário "ignore" e nenhuma chamada a `DisposeItemAsync`. | `RingBufferManager.cs:277-281,337,345-348` | Descartar o item nesse handler (Ação 5). **Corrigido — ver [plano-de-acao.md](./plano-de-acao.md) P0#5.** |
| **F6** | **Média** | A guarda `_scaling` em `ProcessTickAsync` é código morto inalcançável (grep confirma apenas 4 ocorrências, todas consistentes com "sempre falso"). Consequência: um scale-up lento pode deixar a janela de amostragem preenchida com leituras tiradas quase no mesmo instante, degradando a mediana para quase uma única amostra — o que pode disparar um scale-down imediato logo após um scale-up terminar. | `RingBufferManager.cs:64,420-424,458,486,646-662` | Remover o ramo morto, ou mover a guarda para onde possa de fato rodar; marcar com timestamp/descartar amostras obsoletas. |
| **F7** | **Baixa-Média** | `_faultCount` nunca reseta enquanto a capacidade está travada em `MaxCapacity` (só reseta dentro do ramo que exige `CurrentCapacity != MaxCapacity`), então falhas continuam se acumulando; após um scale-down posterior, o próximo timeout de acquire dispara imediatamente o scale para o máximo de novo, produzindo oscilação (flapping). Relacionado à questão do limiar em §4.3. | `RingBufferManager.cs:399-405` | Resetar/limitar o contador independentemente de um scale ser possível no momento. |
| **F8** | **Média** | O ADR001 cita `LockWhenScaling` como a mitigação para quem quer acquire serializado contra a escala; a v5 removeu essa capacidade (`LockWhenScaling` agora só afeta se o *chamador* do `SwitchToAsync` aguarda a conclusão) e a mudança está registrada só em um comentário de código, não no ADR. | ADR001 §36; `RingBufferManager.cs:13-17,224` | Revisar/suceder o ADR001 para declarar a semântica v5; decidir deliberadamente se um mecanismo substituto é necessário. Ver também §4.1. |
| **F9** | **Baixa-Média** | O contrato de retorno documentado de `IRingBufferManualScaleService.SwitchToAsync` ("false quando já na capacidade ou quando um scale já está em andamento") não bate com a implementação (não existe rejeição de "já em andamento" — ele só entra na fila). Mesmo defeito que Usabilidade U-04. | `Commands/IRingBufferManualScaleService.cs:23-26`; `RingBufferManager.cs:386-395` | Implementar a rejeição documentada, ou corrigir a doc — escolher uma (relacionado ao mesmo tipo de alinhamento código/doc de §4.3, com menor impacto). |
| **F10** | **Baixa — suspeita, não reproduzida — ✅ CORRIGIDO (sem teste vermelho/verde)** | A própria guarda `_disposed` do `RingBufferManager.DisposeAsync` segue o mesmo padrão não atômico de F2; um probe de 300 iterações de dispose concorrente produziu 0 exceções (a janela é estreita), então isso é sinalizado como higiene latente, não um bug demonstrado hoje. Uma segunda tentativa durante a correção (1000 iterações, threads dedicadas, ~12 min) também produziu 0 exceções. | `RingBufferManager.cs:62,240-241,283` | Mesma correção com `Interlocked` de F2, mesmo idioma, barato de fazer junto. **Corrigido — ver [plano-de-acao.md](./plano-de-acao.md) P0#3 para o registro explícito de "vermelho não alcançado" (regra 5, passo 3).** |
| **F11** | **Baixa** | `ScaleDownMin` é calculado e propagado, mas nunca lido por `AutoScaleDecision.EvaluateScaleDown` — estado morto (grep confirma exatamente 3 ocorrências, uma sendo um fixture de teste). | `RingBufferBuilder.cs:207,290`; `RingBufferManager.cs:89`; `AutoScaleDecision.cs:55-56` | Remover, ou implementar — não deixar silenciosamente inerte. |

**Nota sobre cobertura de testes:** todo teste de idempotência de dispose na suíte (`RingBufferValueTests.cs:61`, teste de contrato 1.4) testa double-dispose apenas *sequencial*. F2 — o que de fato corrompe o estado do pool — precisa de double-dispose *concorrente* e é invisível para a suíte atual. Nenhum teste em lugar nenhum usa uma factory que lança exceção.

---

## 6. Achados detalhados — Resiliência (comportamento sob falha)

**Veredito do passe: NÃO PRONTO como documentado.** O `doc/guides/usage-rabbitmq.md` promete explicitamente degradação graciosa em caso de perda de conexão; a implementação atual não entrega isso, e o problema não é exclusivo de elástico — reproduz também em buffers `FixedCapacity` simples.

| ID | Severidade | Achado | Localização | Recomendação |
|---|---|---|---|---|
| **R1** | **Crítica — confirmada em execução — ✅ CORRIGIDO** | Mesmo defeito de F1, alcançado por uma falha externa realista (RabbitMQ lança exceção em vez de cancelar). Reproduzido: travamento na inicialização sem exceção/log/métrica quando a factory lança exceção durante o warmup (não limitado por `AcquireTimeout`, que ainda não está em vigor); perda permanente do plano de gestão após uma falha de factory pós-warmup — confirmado que o autoscaler nunca se recupera mesmo depois que a factory volta a ficar saudável; perda silenciosa de capacidade via `Invalidate()` (2 de 3 itens obteníveis enquanto `CurrentCapacity` ainda reporta 3). | `RingBufferManager.cs:513,516,528,363,370,152,158,303-304,382` | Ação 1 (§3). **Corrigido — ver [plano-de-acao.md](./plano-de-acao.md) P0#1.** |
| **R2** | **Alta — confirmada em execução — ✅ CORRIGIDO** | `DisposeAsync` ou trava para sempre (se o warmup nunca completou) ou lança a exceção antiga da factory *antes* de drenar itens do pool e descartar `_lifetime`/`_meter`/`_activitySource` — o shutdown falha e vaza ao mesmo tempo. | `RingBufferManager.cs:251-285` | Ação 5 (§3); limitar com timeout o await de `_warmup.Value` também. **Corrigido pela combinação de P0#2 (o await de `_warmup.Value` já não trava, pois `WarmupCoreAsync` internamente está limitado por `_lifetime.Token`) + P0#5 (limpeza incondicional) — ver [plano-de-acao.md](./plano-de-acao.md).** |
| **R3** | **Alta — confirmada em execução — ✅ CORRIGIDO** | Um callback de `HeartBeat` que bloqueia para a bomba para sempre (o token do `Task.Run` não cancela um delegate em execução) e prende um item do pool permanentemente, com a guarda de timeout pretendida sendo inalcançável. Probe de 4 segundos: 1 invocação em vez de ~7 esperadas, item permanentemente ausente do pool. | `RingBufferManager.cs:623-631,616` | Ação 4 (§3); decidir o que acontece com o item preso (provavelmente `Invalidate()`). **Corrigido com `Invalidate()` no timeout — ver [plano-de-acao.md](./plano-de-acao.md) P0#4.** |
| **R4** | **Alta — confirmada em execução** | O autoscale-on-fault nunca faz scale-up quando `initialCapacity == minCapacity` — uma configuração legal e natural ("começar no piso, crescer sob pressão"). Confirmado: 5 timeouts de acquire consecutivos, capacidade nunca se moveu, com uma factory perfeitamente saudável. Nenhum sample ou exemplo de guia atinge isso hoje, mas é silencioso e total quando se aplica. | `RingBufferManager.cs:403` | Basear a decisão em "próxima capacidade estritamente maior que a atual", não em uma comparação de igualdade de valor; validar em tempo de `Build`. |
| **R5** | **Média — confirmada em execução, atinge o sample publicado** | O prazo do scale-up é a *janela de amostragem* (`SamplesBase`), que também precisa cobrir `delta × latência da factory por item`. O `RingBufferPlusRabbitSample` publicado (`delta=10`, janela de 5s) tolera só 500ms/item — o probe confirma que ele abre 10 canais reais do broker e descarta todos por timeout, ganhando zero capacidade, repetindo a cada ciclo de falha (carga extra sobre um broker já em dificuldade). | `RingBufferManager.cs:504,510,530-535`; `samples/RingBufferPlusRabbitSample/Program.cs:73` | Desacoplar o prazo da janela de amostragem ou validar a relação em `Build`; manter itens parcialmente criados em vez de descartar todos; redimensionar o sample. |
| **R6** | **Média** | O loop do engine processa comandos estritamente em série; um scale-down lento o bloqueia por até `SamplesBase` (30s padrão), período em que `Fault`/`ReplaceOne`/`Switch` não são atendidos — o scale-up por autoscale fica "surdo" por até 30s exatamente no momento em que a carga pode estar subindo de novo. | `RingBufferManager.cs:357-361,548,543` | Não bloquear o loop de comandos por disponibilidade de itens; tornar o scale-down oportunista em vez de esperar por uma quantidade fixa. |
| **R7** | **Média** | O orçamento de falhas reseta *antes* de uma tentativa de scale-up rodar, então um scale-up que depois falha (timeout, R5) ainda queima o orçamento — o buffer precisa acumular um ciclo de falhas inteiro novo antes de tentar de novo, agravando a degradação exatamente quando não deveria. O contador também nunca decai com o tempo (só reseta em um scale-up disparado), então falhas isoladas e não relacionadas, espaçadas por dias, ainda podem disparar um scale-up muito depois que a carga que as causou já passou — ver também F7. | `RingBufferManager.cs:399-405` | Resetar o orçamento só após um movimento *bem-sucedido*; fazer o contador decair ao longo de uma janela de tempo ligada a `SamplesBase`. |
| **R8** | **Média** | Operações de scale que falharam/foram desfeitas são registradas de forma idêntica às bem-sucedidas em métricas (`scale.operations`, `scale.duration`) e traces (sem `ActivityStatusCode.Error`). Durante uma instabilidade, um dashboard lê "escalou 40 vezes, saudável" quando a verdade é 40 falhas consecutivas e zero mudança de capacidade — exatamente o sinal errado no momento exato em que um operador precisa do sinal certo. | `RingBufferManager.cs:452-455,484-497` | Ação 7 (§3). |
| **R9** | **Baixa** | `ScaleDownMin` calculado, documentado (com fórmula específica na doc XML), e nunca lido em tempo de execução. Mesmo defeito de Estabilidade F11, visto pelo lado da documentação. | `Commands/IRingBufferElasticBuilder.cs`; `RingBufferBuilder.cs:284-290` | Remover a configuração morta e seu parágrafo de doc, ou implementá-la. |
| **R10** | **Baixa — confirmada em execução** | O limiar de falhas documentado está com off-by-one (ver §4.3), e o guia de RabbitMQ/autoscale nomeia o tipo de exceção errado para falhas de validação em tempo de `Build` (`IndexOutOfRangeException` documentado, `InvalidOperationException` de fato lançado — confirmado por teste). | `RingBufferManager.cs:400`; `RingBufferBuilder.cs:268-279` | Ver §4.3 para o limiar; correção de uma palavra para o tipo de exceção (também Usabilidade U-21). |
| **R11** | **Baixa** | A própria bomba de heartbeat da biblioteca chama `AcquireAsync` internamente; sob carga legítima sustentada, o timeout do próprio heartbeat contribui para o orçamento de falhas do autoscale — um scale-up autoinfligido disparado pelo health check interno do pool, não pela demanda de consumidores. | `RingBufferManager.cs:615,170-181` | Isentar acquires de heartbeat do contador de falhas, ou marcá-los separadamente nas métricas. |
| **R12** | **Baixa — suspeita, não reproduzida — ✅ CORRIGIDO** | Um item devolvido via `TurnbackAsync` concorrentemente com o dispose pode ser silenciosamente descartado sem ser disposed (janela estreita; não reproduzida no probing). Mesmo caminho de código de F5. | `RingBufferManager.cs:337,277-281,345` | Coberto pela Ação 5 (§3). **Corrigido junto com F5 — mesmo código, mesma correção — ver [plano-de-acao.md](./plano-de-acao.md) P0#5.** |

---

## 7. Achados detalhados — Usabilidade (superfície de API e documentação)

**Veredito do passe: PRONTO COM RESSALVAS.** A profundidade da documentação está genuinamente acima da média para uma biblioteca deste porte — 10 guias orientados a tarefas, ADRs registrando o raciocínio, uma referência gerada por membro, zero links quebrados em 100 arquivos markdown, e todos os 5 samples compilam limpos contra a superfície v5 real. O problema está concentrado no punhado de coisas que um adotante novo toca primeiro.

### Crítica

| ID | Achado | Localização |
|---|---|---|
| **U-01 ✅ CORRIGIDO** | O Quickstart do README usa uma variável `cancellation` não declarada — confirmado que falha na compilação como impresso (`CS0103` ×2). Este também é o readme do pacote NuGet, ou seja, o primeiro código que qualquer visitante do nuget.org vê. **Corrigido:** adicionada `CancellationToken cancellation = default;`; confirmado que compila e executa (probe verbatim). | `README.md:36-53` |
| **U-02 ✅ CORRIGIDO** | O guia de DI instrui a injetar `IRingBufferManualScaleService<T>` diretamente — confirmado que lança `InvalidOperationException` na resolução, porque `AddRingBuffer` só registra `IRingBufferService<T>`. O próprio sample do repositório faz deliberadamente o oposto (pattern-matching) e cita o ADR007 como motivo. **Corrigido:** removida a instrução de injeção direta; texto agora diz que só `IRingBufferService<T>` é registrado e orienta o pattern-match (igual ao sample), com a correção adicional de que é o `SwitchToAsync` que lança, não o cast/pattern-match em si (que sempre sucede, pois `RingBufferManager<T>` sempre implementa `IRingBufferManualScaleService<T>` — confirmado em `RingBufferBuilder.cs:130,158,179` todos chamando o mesmo `BuildCore`). | `doc/guides/usage-dependency-injection.md:46,54`; contradito por `samples/RingBufferPlusApiSample/Controllers/WeatherForecastController.cs:49-56` |

### Alta

| ID | Achado | Localização |
|---|---|---|
| **U-03** | A descrição do alvo de scale-up no guia de autoscale é ambígua, e uma leitura natural é o inverso exato do comportamento real. | `doc/guides/usage-elastic-autoscale.md:29` |
| **U-04** | O contrato de retorno documentado de `SwitchToAsync` é o comportamento v4 literal (uma rejeição de "já em andamento" que o próprio ADR007 cita como o defeito que removeu). | `Commands/IRingBufferManualScaleService.cs:23-26` |
| **U-05** | "Retorna assim que agendado... isso acontece rápido e sempre" subestima que um `SwitchToAsync` atrás de um scale em andamento pode bloquear ~30s independentemente de `LockWhenScaling`. | `doc/guides/usage-lock-when-scaling.md:32`; `usage-elastic-manual-scale.md:35` |
| **U-06** | O segundo cenário do sample RabbitMQ é um no-op (`LockWhenScaling` não tem efeito no builder de autoscale) enquanto seu comentário afirma demonstrar a mudança de comportamento de lock da v5, usando texto do v4. | `samples/RingBufferPlusRabbitSample/Program.cs:87,93-94` |
| **U-07** | Limiar de falhas do autoscale documentado como off-by-one em **quatro** locais separados, incluindo a seção do CHANGELOG posicionada como única referência de migração. Ver §4.3. | `Commands/IRingBufferElasticBuilder.cs:98`; referência gerada; `usage-elastic-autoscale.md:29,37`; `CHANGELOG.md:19` |
| **U-08** | A "única referência de migração" do CHANGELOG omite as duas quebras v4→v5 com maior chance de atingir código real: `SwitchToAsync` removido inteiramente de `IRingBufferService<T>`, e as três interfaces de builder renomeadas sem nenhum mapeamento antigo→novo. | `CHANGELOG.md:16` vs. `git diff v.4.0.1` real |
| **U-21** | O guia de autoscale nomeia o tipo de exceção errado para falhas de validação em tempo de `Build`. | `doc/guides/usage-elastic-autoscale.md:42` |

### Média

| ID | Achado | Localização |
|---|---|---|
| **U-09** | A doc XML de `AcquireAsync` não menciona nenhuma exceção, mas ele lança `OperationCanceledException`/`ObjectDisposedException` em caminhos para os quais o guia de DI ativamente encaminha as pessoas (tokens de request-abort). | `Commands/IRingBufferService.cs:54-60`; `RingBufferManager.cs:151,189-201` |
| **U-10** | `LockWhenScaling()` é um método público, visível no IntelliSense, no builder de autoscale, que é um no-op documentado — ver §4.1. | `Commands/IRingBufferAutoScaleBuilder.cs:65-73` |
| **U-11** | 12 operações distintas do builder são publicadas como 35 declarações em 4 interfaces com docs XML copiadas à mão (um contribuinte direto para a inconsistência de U-07) e 35 páginas de referência gerada sem nenhuma sinalização entre elas. Não é uma violação do ADR — o esboço do ADR era hedged — mas o formato publicado e sua justificativa não estão documentados. | `IRingBufferBuilder.cs`, `IRingBufferFixedBuilder.cs`, `IRingBufferElasticBuilder.cs`, `IRingBufferAutoScaleBuilder.cs` |
| **U-12** | Nenhum exemplo de código em lugar nenhum para `OnError` (verifiquei todos os guias, README, todos os 5 samples); `Invalidate()` — a preocupação central do mundo real para um pool de conexão/canal — recebe uma frase e dois call sites de sample não explicados. Ambos são exatamente o que um adotante precisa quando a produção dá errado. | — |
| **U-13** | O timeout padrão de 30 segundos para escala (`baseTimer`) nunca é nomeado onde importa (o guia de lock-when-scaling); no modo de escala manual, `numberSamples` é validado em `Build` mas não tem nenhum efeito em runtime (a bomba de amostragem nunca inicia sem autoscale). | `doc/guides/usage-lock-when-scaling.md:32,37`; `RingBufferManager.cs:325-327`; `RingBufferBuilder.cs:268-279` |
| **U-14** | Duas registrações de DI do mesmo `T` sob nomes diferentes resolvem silenciosamente para a última registrada, em injeção de construtor simples — confirmado ao rodar. Não coberto pelo aviso do guia de DI sobre mesmo-nome/mesmo-`T`. | `HostingExtensions.cs:33` |
| **U-22** | Um `WarmupAsync` que falha destrói permanentemente a instância (exceção cacheada) sem nenhuma menção na doc XML pública, enquanto todo guia recomenda registro como singleton no DI. Ver §4.2. | `RingBufferManager.cs:136`; `Commands/IRingBufferService.cs:62-71` |

### Baixa

| ID | Achado | Localização |
|---|---|---|
| U-15 | O parêntese da doc XML de `ElasticCapacity` está aritmeticamente errado (30s/100 amostras ≠ 100ms; são 300ms, corretamente afirmado uma linha acima). | `IRingBufferBuilder.cs:82` |
| U-16 | O "Default true" da doc de `LockWhenScaling` lê como o padrão da configuração; é o padrão do parâmetro — a configuração é `false` a menos que o método seja chamado. | `IRingBufferElasticBuilder.cs:69` |
| U-17 | O comportamento do contador de falhas na capacidade máxima não está documentado (mesmo código de Estabilidade F7). | `RingBufferManager.cs:399-405` |
| U-18 | A referência gerada rotula errado um link de namespace (rótulo vs. alvo não batem; o link em si resolve normalmente). | `src/docs/assemblies/Microsoft.Extensions.DependencyInjection/HostingExtensions.md:22` |
| U-19 | O "O que há de novo" do README descreve a v5.0.0 como puras breaking changes; a única funcionalidade aditiva (observabilidade nativa) não é mencionada nessa seção. | `README.md:20-24` |
| U-20 | `CONTRIBUTING.md` nunca declara comandos de build/teste nem que `src/docs/**` precisa ser regenerado após mudanças na doc XML — diretamente relevante dado que U-07/U-15 estão duplicados nessa árvore gerada. | `CONTRIBUTING.md:78-90` |
| U-23 | As duas sobrecargas de `RingBuffer<T>.New` discordam sobre nullability do mesmo argumento lógico. | `RingBufferExtension.cs:24,39` |
| U-24 | `RingBufferDefault.Capacity = 2` é documentado como "a capacidade padrão", mas é inalcançável — todo caminho até `Build`/`BuildWarmupAsync` exige definir a capacidade explicitamente. | `RingBufferDefault.cs:38-41`; `RingBufferBuilder.cs:101-117` |

**Verificado e considerado correto** (o que delimita a lista de correções — isto *não* são achados): todos os 7 trechos de código dos guias compilam contra a superfície v5 real; `concepts.md` e `usage-observability.md` são totalmente precisos frente ao código; `usage-heartbeat.md` e `usage-fixed-capacity.md` batem exatamente com a implementação; a exclusividade em tempo de compilação para a qual o ADR007 foi escrito (nenhum `SwitchToAsync` no escopo após `AutoScaleAcquireFault`) de fato funciona; os requisitos de processo do ADR004 (suporte só para 5.x, política de `[Obsolete]`-antes-de-remover) estão ambos satisfeitos em `SECURITY.md`/`CONTRIBUTING.md`; zero links relativos ou de âncora quebrados em todos os 100 arquivos markdown; a referência de API gerada está sincronizada com a doc XML atual; todos os cinco samples compilam e, juntos, cobrem fixo+heartbeat, ambas as variantes de escala manual, autoscale, DI no ASP.NET Core, e RabbitMQ.

---

## 8. Metodologia

Três passes de auditoria rodaram de forma independente e em paralelo, cada um com seu próprio escopo e sem visibilidade sobre o trabalho ou as conclusões dos outros, seguindo os eixos combinados antes de começar:

1. **Estabilidade** — `Core/RingBufferManager.cs`, `ScaleSwitch.cs`, `Core/AutoScaleDecision.cs`, `Core/LogMessageBackground.cs`, testes de concorrência/escala, ADR001 (modelo de concorrência), ADR005 (dispose assíncrono).
2. **Resiliência** — as interfaces de builder de autoscale/elástico, `HostingExtensions.cs`, `RingBufferExtension.cs`, ADR003 (algoritmo de autoscale), ADR008 (observabilidade), os samples de RabbitMQ e trigger-scale.
3. **Usabilidade** — toda a superfície pública de builder/serviço, `RingBufferDefault.cs`, `RingBufferValue.cs`, README, os 10 guias, ADR004/ADR007, CHANGELOG, todos os 5 samples.

Achados marcados como **"confirmado em execução"** foram reproduzidos com probes de console descartáveis em um diretório temporário, cada um referenciando a biblioteca compilada via referência de projeto — **o repositório em si nunca foi modificado**. Achados marcados como **"suspeita, não reproduzida"** são conclusões só de leitura de código que o passe responsável não conseguiu ou não tentou disparar de forma determinística; estão sinalizados como riscos de higiene, não bugs demonstrados. Nenhum achado abaixo do status "suspeita" é afirmado como fato.

Onde dois passes descreveram a mesma região de código subjacente por ângulos diferentes (a causa raiz da §2; a região do contador de falhas do autoscale tocada por F7/R7/U-07; o `LockWhenScaling` tocado por F8/U-06/U-10), ambas as leituras foram mantidas e referenciadas cruzadamente em vez de fundidas em uma só, já que cada uma contribui com evidência que a outra não tinha.

---

## 9. Veredito final

**Segure o posicionamento de "pronto para produção" da v5.0.0 como está publicada hoje.** O fato de maior prioridade neste relatório: uma factory que lança exceção — o modo de falha normal para exatamente os recursos que esta biblioteca existe para agrupar (conexões de banco de dados, canais RabbitMQ) — mata o engine permanentemente e pode travar todo método público assíncrono, incluindo o que deveria encerrá-lo de forma limpa. Isso reproduz com uma linha de código de usuário comum, reproduz em um buffer de capacidade fixa simples, e nada na suíte de testes atual o exercita.

Dito isso, isto não é um redesenho. Os dois passes técnicos confirmaram, de forma independente, que a aposta arquitetural central do rewrite v5 — um único consumidor em canal que possui todo o estado de escala — de fato funciona: nenhuma atualização perdida, nenhum estado corrompido, nenhum deadlock na lógica de escala em si. O que falta é um perímetro defensivo que representa cerca de um dia de trabalho focado (uma fronteira de exceção, duas trocas por `Interlocked`, um padrão de timeout corrigido, um bloco `finally`) mais quatro novos testes de regressão. A documentação, de forma semelhante, precisa de cerca de um dia de ajustes só de texto, sem mudanças de código, para passar de "pronta com ressalvas" para "pronta".

**Sequência recomendada:** implementar o P0 (§3) seguindo o protocolo vermelho/verde — escrever primeiro o teste da factory que lança exceção, confirmar que falha pelo motivo certo (um travamento, não um erro de compilação), então corrigir e confirmar verde — publicar como 5.0.1 ou uma nova v5.0.0, e só então retomar a promoção do release. O P1 pode seguir no mesmo patch ou no próximo. O P2 precisa da sua decisão (§4) antes que qualquer um mexa nesse código. O P3 é backlog normal.
