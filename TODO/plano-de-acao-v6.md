# Plano de ação — auditoria pré-release v6.0.0

Log iteração-por-iteração do que foi feito em resposta aos achados de
`relatorio-auditoria-v6.md`. Granularidade de execução, não de round (isso fica em
`acompanhamento-convergencia-v6.md`).

---

## Round 1 — 2026-08-23/24

Passes das 6 frentes confirmadas disparadas (com retentativa para 5 delas, ver
`relatorio-auditoria-v6.md`). Todas concluídas: 18 achados distintos (1 Crítico,
4 Alto, 8 Médio, 5 Baixo).

**Corrigidos nesta rodada (os 3 priorizados, a pedido do usuário):**

1. **[Crítico, usabilidade] README.txt "undone" claim** — corrigido diretamente
   (fix de doc, sem red/green, texto trocado para bater com `usage-lock-when-scaling.md`/
   ADR010V01). Busca em todo o repo (`grep -i undone`) confirmou que era a única
   ocorrência desatualizada.
2. **[Alto, resiliência] DisposeAsync pode travar para sempre via heartbeat pump** —
   trade-off apresentado ao usuário antes de implementar (reverte parte de uma
   decisão anterior documentada como deliberada); usuário escolheu a opção cirúrgica
   (delimitar só a chamada do pump em `RunHeartbeatAsync`, sem tocar em
   `TurnbackAsync` nem no comportamento de chamadores externos). Red/green: novo teste
   `HeartBeatInvalidate_WhenItemDisposeHangs_ManagerDisposeAsyncStillReturnsPromptly`
   falhou por 2s de timeout (motivo previsto), passou após o fix. Reutiliza o padrão
   já existente (`_pendingHeartbeatDisposals` + grace period de `PulseHeartBeat`) do
   branch de timeout irmão no mesmo método.
3. **[Alto, resiliência] AddRingBuffer\<T\> acopla falhas entre buffers via GetServices** —
   antes de corrigir, confirmei que `usage-dependency-injection.md` documenta
   explicitamente o padrão `IEnumerable<IRingBufferService<T>>` para múltiplos
   buffers do mesmo T, que depende do registro ser singleton "plano" — a correção
   não podia simplesmente trocar tudo para keyed sem quebrar isso. Fix: registro
   passou a ser um singleton keyed (por `buffername`) + um singleton plano que só
   encaminha para o keyed (preserva o `IEnumerable`/"last-wins" documentado sem
   mudança); `RingBufferWarmupHostedService<T>` resolve via `GetKeyedService`, não
   mais enumerando+filtrando. Red/green: novo teste
   `HostedService_StartAsync_ForOneBuffer_DoesNotConstructAnUnrelatedBufferOfTheSameType`
   falhou propagando a exceção do buffer "bad" (motivo previsto), passou após o fix.
   Teste pré-existente (`HostedService_StartAsync_WarmsUpTheNamedBufferWithItsOwnToken`)
   atualizado para registrar via `AddKeyedSingleton`, já que o mecanismo mudou.

Verificado: 177/177 testes net10.0 (era 175, +2 novos), build limpo nos 3 TFMs
(net8.0/9.0/10.0) para toda a solução (lib + 3 TFMs + samples + benchmarks +
gerador de docs). Nada commitado ainda - aguardando decisão do usuário sobre commit.

**Continuação — 2026-08-24, "próximos achados":**

Corrigidos mais 11 achados/instâncias (2 Alto, 5 Médio, 2 Baixo, +2 novas
ocorrências da mesma classe encontradas via grep durante os fixes):

- Doc: `usage-rabbitmq.md` (Fault/Tick incorreto), `overview.md` (AutoScaleDecision
  removida), `usage-observability.md` (link morto + taxonomia de trigger + ADR
  desatualizada), `concepts.md` (ADRs supersedidas), `ADR007V03` (LockWhenScaling
  "stays removed" — falso; usuário confirmou corrigir o texto do ADR diretamente,
  tratando como correção factual, não nova decisão) — todos fixes diretos, sem
  red/green (mudança de texto, não de comportamento).
- Código, com red/green completo (stash temporário da fonte do fix, confirma
  falha pelo motivo certo, restaura, confirma verde): validação de piso para
  `PulseHeartBeat` em `ValidateBuild` (Médio), interpolação de `scaleTrigger` nos
  4 logs de `DispatchScaleUp`/`DispatchScaleDown` (Médio), tag `trigger` em
  `scale.duration` (Baixo).
- Código, sem red/green (robustez teórica, sem cenário de falha reproduzível):
  `_disposed` → `volatile bool`.
- Doc + XML doc, escopo maior que o achado original: `OnError` "substitui, não
  soma" o log do `Logger` — a frase enganosa estava duplicada em 3 interfaces
  (`IRingBufferBuilder`/`IRingBufferFixedBuilder`/`IRingBufferElasticBuilder`), não
  só uma; docs de API regeneradas corretamente (rebuild lib → rebuild gerador →
  gerar, nessa ordem).

Verificado: 180/180 testes net10.0 (era 177, +3 novos), build limpo em toda a
solução (3 TFMs, samples, benchmarks, gerador de docs) a cada etapa.

**Ainda não corrigidos** (aguardando decisão do usuário — ver próxima mensagem):
2 Médio candidatos pendentes de corroboração (pin parcial, fan-out de Tasks),
1 Médio de design (telemetria do Monitor sem contexto de decisão), 2 Baixo de
complexidade (estruturas de dados do algoritmo Monitor, valor questionável nos
defaults atuais), gaps de cobertura de benchmark (recomendação, não um fix de
código existente).

## Round 2 — 2026-08-24

Todas as pendências acima resolvidas nesta rodada (corroborações rodadas, decisões
tomadas — ver `relatorio-auditoria-v6.md` Round 1 para os detalhes). Round 2 em si:
6 frentes fresh, mesmo enquadramento do Round 1, focadas em regressão + achados
novos. 3/6 precisaram de retentativa por infra (mesma falha recorrente de stall/erro
de API já vista no Round 1).

**Achados fechados nesta rodada** (12 total: 2 Alto, 6 Médio, 4 Baixo — ver
`relatorio-auditoria-v6.md` seção Round 2 para os detalhes completos de cada um):

- 2 Alto: guard `IsEnabled` desprotegido (violação do invariante F23) — introduzido
  pela própria correção desta sessão, achado pela estabilidade; `AddHostedService`
  deduplicando por tipo (pré-existente desde ADR007V03, nunca antes pego) — só o 1º
  buffer de cada T recebia warmup automático, achado pela resiliência após ~65min
  de investigação real.
- 2 Médio de código: falha tardia do dispose adiado do heartbeat não observada
  (mesmo padrão `ContinueWith` da F12/F15 aplicado); duas imprecisões de mensagem
  de log corrigidas junto.
- Doc: 4 achados de usabilidade (guias desatualizados face aos fixes do Round 1),
  overclaim do log do Monitor corroborado independentemente por 2 frentes com prova
  empírica, assimetria target/trigger nos logs, e a recomendação de
  `IEnumerable<IRingBufferService<T>>` substituída por `[FromKeyedServices]`
  (verificado empiricamente que funciona por construtor plano em qualquer host).
- 2 achados pré-existentes ao v6 (não regressão desta sessão, mas fechados por
  decisão do usuário): mesma classe do bug do guard `IsEnabled` em
  `RingBufferBuilder.cs`; `LogError` do builder passando `null` em vez da exceção
  real para sinks estruturados.
- Confirmação empírica de desempenho (Baixo, sem regressão): o fix do `LogMessage`
  guard mede ~39x mais rápido no caminho gated, mas impacto absoluto desprezível.
  Benchmark novo (`MonitorTickLogGuardBenchmarks.cs`) mantido no repo.

Verificado: 186/186 testes net10.0 (era 183, +3 novos), build limpo (0 warnings,
0 errors) em toda a solução a cada etapa. Nenhum achado do Round 2 ficou em aberto
sem decisão.
