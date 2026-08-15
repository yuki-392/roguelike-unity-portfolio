using System;
using System.Collections.Generic;
using System.Linq;
using Roguelike.Domain.Behaviors;
using Roguelike.Domain.Models;
using Roguelike.Domain.Rng;
#nullable enable

namespace Roguelike.Domain.Battle
{
    // 戦闘1回分のルール処理を集約する純粋関数群。
    // BattleState は immutable record なので、各メソッドは state を受け取り新しい state を返す形で統一している
    // （Unity側のMonoBehaviourからは呼ばれず、Presentation層が結果のstateを描画に反映する想定）。
    public static class BattleEngine
    {
        private const int InitialDrawCount = 5;
        private const int TurnDrawCount = 5;
        private const int MaxHandSize = 9;
        private const int MaxLogEntries = 20;

        public static BattleState StartBattle(
            PlayerCombatState player,
            IReadOnlyList<EnemyCombatState> enemies,
            IRng rng,
            IReadOnlyList<RelicDefinition>? relics = null)
        {
            var ownedRelics = relics ?? Array.Empty<RelicDefinition>();
            var maxEnergyBonus = RelicMaxEnergyBonus(ownedRelics);
            // 前の戦闘の手札・捨て札が残っている状態で呼ばれる可能性があるため、
            // 山札・手札・捨て札を一旦すべて合流させてから引き直す（デッキ内容自体は変えない）。
            var combined = player.DrawPile
                .Concat(player.Hand)
                .Concat(player.DiscardPile)
                .ToArray();
            var resetPlayer = player with
            {
                Block = 0,
                Energy = player.MaxEnergy + maxEnergyBonus,
                MaxEnergy = player.MaxEnergy + maxEnergyBonus,
                Hand = Array.Empty<CardDefinition>(),
                DrawPile = rng.Shuffled(combined),
                DiscardPile = Array.Empty<CardDefinition>(),
                Statuses = new Dictionary<StatusKind, int>(),
            };
            var state = BattleState.Create(resetPlayer, enemies, ownedRelics);
            state = ApplyBattleStartRelics(state, rng);
            state = ApplyBattleStartStatusBlock(state);
            state = ApplyTurnStartRelics(state);
            return DrawCards(state, InitialDrawCount, rng);
        }

        private static BattleState ApplyBattleStartStatusBlock(BattleState state)
        {
            var hardening = Status(state.Player.Statuses, StatusKind.Hardening);
            var armor = Status(state.Player.Statuses, StatusKind.Armor);
            return state with
            {
                Player = state.Player with
                {
                    Block = state.Player.Block + hardening + armor +
                        HardeningEffectBonus(state),
                },
            };
        }

        private static BattleState ApplyBattleStartRelics(
            BattleState state,
            IRng rng)
        {
            var next = state;
            foreach (var relic in state.Relics)
            {
                next = relic.Effect switch
                {
                    BlockOnBattleStartRelicEffect block =>
                        AddPlayerBlock(next, block.Amount),
                    BlockOnEliteBattleStartRelicEffect block when
                        next.Enemies.Any(enemy => enemy.Tier == EnemyTier.Elite) =>
                        AddPlayerBlock(next, block.Amount),
                    BattleStartPlayerStatusRelicEffect status =>
                        ApplyRelicPlayerStatus(next, status.Status, status.Stacks),
                    BattleStartPlayerStatusesRelicEffect statuses =>
                        ApplyRelicPlayerStatuses(next, statuses),
                    PoisonAllEnemiesOnBattleStartRelicEffect poison =>
                        ApplyStatusToEnemies(
                            next,
                            new PoisonStatusEffect(),
                            poison.Stacks,
                            AttackTarget.AllEnemies,
                            includeRelicApplicationBonus: true),
                    PoisonRandomEnemyOnBattleStartRelicEffect poison =>
                        ApplyRandomEnemyPoison(next, poison.Stacks, rng),
                    ExtraEnergyOnFirstTurnRelicEffect energy => next with
                    {
                        Player = next.Player with
                        {
                            Energy = next.Player.Energy + energy.Amount,
                        },
                    },
                    _ => next,
                };
            }
            return next;
        }

        private static BattleState ApplyTurnStartRelics(BattleState state)
        {
            var next = state;
            foreach (var relic in state.Relics)
            {
                if (relic.Effect is BlockOnTurnStartIfEmptyRelicEffect block &&
                    next.Player.Block == 0)
                    next = AddPlayerBlock(next, block.Amount);
                if (relic.Effect is BlockOnThirdTurnStartRelicEffect thirdTurn &&
                    next.PlayerTurnNumber == 3)
                    next = AddPlayerBlock(next, thirdTurn.Amount);
            }
            return next;
        }

        private static int RelicMaxEnergyBonus(
            IReadOnlyList<RelicDefinition> relics) =>
            relics.Sum(relic => relic.Effect is MaxEnergyBonusRelicEffect effect
                ? effect.Amount
                : 0);

        private static BattleState ApplyRelicAfterCardPlay(
            BattleState state,
            CardDefinition card,
            IRng rng)
        {
            var next = state;
            if (card.Category == CardCategory.Skill)
            {
                foreach (var relic in state.Relics)
                {
                    if (relic.Effect is DrawChanceOnSkillCardRelicEffect draw &&
                        rng.NextDouble() < draw.Chance)
                        next = DrawCards(next, draw.Count, rng);
                }
            }
            if (card.Category == CardCategory.Defense)
            {
                foreach (var relic in state.Relics)
                {
                    if (relic.Effect is DrawChanceOnBlockCardRelicEffect draw &&
                        rng.NextDouble() < draw.Chance)
                        next = DrawCards(next, draw.Count, rng);
                }
            }
            if (card.Category == CardCategory.Original &&
                !state.OriginalCardUsedThisBattle)
            {
                next = next with { OriginalCardUsedThisBattle = true };
                foreach (var relic in state.Relics)
                {
                    if (relic.Effect is DrawOnOriginalCardFirstUseRelicEffect draw)
                        next = DrawCards(next, draw.Count, rng);
                }
            }
            return next;
        }

        private static BattleState ApplyZeroCostRelics(BattleState state)
        {
            var next = state;
            foreach (var relic in state.Relics)
            {
                if (relic.Effect is not DamageAllOnZeroCostCountRelicEffect effect ||
                    effect.CountThreshold <= 0 ||
                    next.ZeroCostCardsPlayedThisBattle % effect.CountThreshold != 0)
                    continue;
                foreach (var index in Enumerable.Range(0, next.Enemies.Count)
                    .Where(index => next.Enemies[index].CurrentHp > 0))
                    next = DamageEnemy(next, index, effect.Damage, logPlayerDamage: true);
            }
            return next;
        }

        private static BattleState ApplyRandomEnemyPoison(
            BattleState state,
            int stacks,
            IRng rng)
        {
            var living = state.Enemies
                .Select((enemy, index) => (enemy, index))
                .Where(item => item.enemy.CurrentHp > 0)
                .Select(item => item.index)
                .ToArray();
            if (living.Length == 0)
                return state;
            var index = living[rng.NextIntInclusive(0, living.Length - 1)];
            return ApplyStatusToEnemies(
                state,
                new PoisonStatusEffect(),
                stacks,
                AttackTarget.Single,
                includeRelicApplicationBonus: true,
                targetIndex: index);
        }

        private static BattleState ApplyRelicPlayerStatus(
            BattleState state,
            StatusKind kind,
            int stacks)
            => ApplyPlayerStatus(
                state,
                StatusEffectFor(kind),
                stacks);

        private static BattleState ApplyRelicPlayerStatuses(
            BattleState state,
            BattleStartPlayerStatusesRelicEffect effect)
        {
            var next = ApplyRelicPlayerStatus(
                state, effect.FirstStatus, effect.FirstStacks);
            return ApplyRelicPlayerStatus(
                next, effect.SecondStatus, effect.SecondStacks);
        }

        private static int RelicBonusMuscleOnGain(BattleState state) =>
            state.Relics.Sum(relic => relic.Effect is BonusMuscleOnGainRelicEffect effect
                ? effect.Amount
                : 0);

        private static int HardeningEffectBonus(BattleState state) =>
            state.Relics.Sum(relic => relic.Effect is HardeningEffectBonusRelicEffect effect
                ? effect.Amount
                : 0);

        public static BattleState PlayCard(BattleState state, string cardId, IRng rng)
        {
            if (state.Phase != BattlePhase.Battle || state.Turn != TurnOwner.Player)
                return state;
            if (state.PendingDiscardCount > 0)
                return state;
            if (state.PendingConsumableExhaust)
                return state;

            var card = state.Player.Hand.FirstOrDefault(candidate => candidate.Id == cardId);
            if (card == null)
                return state;

            var effectiveCost = EffectiveCost(state, card);
            if (state.Player.Energy < effectiveCost)
                return state with
                {
                    Log = AddLog(state.Log, "エナジーが足りません"),
                };

            // VariableCardCost（残りエネルギーを全消費するタイプ）のカードは、
            // 「消費した分だけ効果が伸びる」効果（EnergyScaledAttackCardEffect等）が後段の
            // ApplyEffect で必要とするため、消費前のエネルギー量をここで保持しておく。
            var variableEnergy = card.Cost is VariableCardCost && effectiveCost > 0
                ? state.Player.Energy
                : 0;
            var next = state with
            {
                Player = state.Player with { Energy = state.Player.Energy - effectiveCost },
                NextCardCostReduction = 0,
                VariableEnergySpent = variableEnergy,
                Log = AddLog(
                    state.Log, $"プレイヤーが{card.Name}をプレイした。"),
            };

            foreach (var effect in card.Effects)
                next = ApplyEffect(next, effect, rng, card);
            next = ApplySpecialOrbPostPlay(next, card, rng);
            next = ApplyRelicAfterCardPlay(next, card, rng);

            var hand = next.Player.Hand.Where(candidate => candidate.Id != card.Id).ToArray();
            var destination = card.Exhaust
                ? next.Player.ExhaustPile.Concat(new[] { card }).ToArray()
                : next.Player.DiscardPile.Concat(new[] { card }).ToArray();
            next = next with
            {
                Player = card.Exhaust
                    ? next.Player with { Hand = hand, ExhaustPile = destination }
                    : next.Player with { Hand = hand, DiscardPile = destination },
                CardsPlayedThisTurn = next.CardsPlayedThisTurn + 1,
                AttackCardsPlayedThisTurn = next.AttackCardsPlayedThisTurn +
                    (card.Category == CardCategory.Attack ? 1 : 0),
                SkillCardsPlayedThisTurn = next.SkillCardsPlayedThisTurn +
                    (card.Category == CardCategory.Skill ? 1 : 0),
            };

            if (effectiveCost == 0)
            {
                next = next with
                {
                    ZeroCostCardsPlayedThisBattle =
                        next.ZeroCostCardsPlayedThisBattle + 1,
                };
                next = ApplyZeroCostRelics(next);
            }

            next = AppendDefeatLogs(state, next);
            next = CheckBattleEnd(next);
            return ResolveForcedDiscard(next);
        }

        public static BattleState SelectDiscardCard(BattleState state, string cardId)
        {
            if (state.PendingDiscardCount <= 0)
                return state;

            var card = state.Player.Hand.FirstOrDefault(candidate =>
                candidate.Id == cardId);
            if (card == null)
                return state;

            var next = state with
            {
                Player = state.Player with
                {
                    Hand = state.Player.Hand.Where(candidate =>
                        candidate.Id != cardId).ToArray(),
                    DiscardPile = state.Player.DiscardPile
                        .Concat(new[] { card }).ToArray(),
                },
                PendingDiscardCount = state.PendingDiscardCount - 1,
                Log = AddLog(state.Log, $"プレイヤーが{card.Name}を捨てた。"),
            };
            return ResolveForcedDiscard(next);
        }

        public static BattleState UsePotion(
            BattleState state,
            PotionDefinition potion,
            IRng rng)
        {
            if (state.Phase != BattlePhase.Battle || state.Turn != TurnOwner.Player ||
                potion.Effect is TransformAfterBattlePotionEffect)
                return state;
            if (potion.Effect is ExhaustCardForBattlePotionEffect &&
                state.Player.Hand.Count == 0 &&
                state.Player.DrawPile.Count == 0 &&
                state.Player.DiscardPile.Count == 0)
                return state;

            var next = state with
            {
                Log = AddLog(state.Log, $"プレイヤーが{potion.Name}を使った。"),
            };
            next = potion.Effect switch
            {
                BlockPotionEffect block => AddPlayerBlock(next, block.Amount),
                GainEnergyPotionEffect energy => next with
                {
                    Player = next.Player with
                    {
                        Energy = next.Player.Energy + energy.Amount,
                    },
                },
                DamageAllEnemiesPotionEffect damage => ApplyPotionDamage(
                    next, damage.Amount, PotionAttackTarget(damage.Target)),
                DamageSingleEnemyPotionEffect damage => ApplyPotionDamage(
                    next, damage.Amount, PotionAttackTarget(damage.Target)),
                ApplyWeakPotionEffect weak => ApplyStatusToEnemies(
                    next,
                    new WeakStatusEffect(),
                    weak.Stacks,
                    PotionAttackTarget(weak.Target)),
                ApplyPoisonPotionEffect poison => ApplyStatusToEnemies(
                    next,
                    new PoisonStatusEffect(),
                    poison.Stacks,
                    PotionAttackTarget(poison.Target),
                    includeRelicApplicationBonus: false),
                DrawPotionEffect draw => DrawCards(next, draw.Count, rng),
                GainStrengthPotionEffect strength => ApplyPlayerStatus(
                    next, new MuscleStatusEffect(), strength.Stacks),
                TemporaryStrengthPotionEffect strength => ApplyTemporaryMuscle(
                    next, strength.Stacks),
                HealPercentPotionEffect heal => HealPlayerPercent(
                    next, heal.Percent),
                GainMaxHpAndHealPercentPotionEffect heal => GainMaxHpAndHeal(
                    next, heal.MaxHp, heal.HealPercent),
                RemoveAllEnemyBlockPotionEffect => ClearEnemyBlocks(next),
                ClearDebuffsPotionEffect => ClearPlayerDebuffs(next),
                DoublePoisonPotionEffect doublePoison => DoubleTargetPoison(next, doublePoison.Target),
                ExhaustCardForBattlePotionEffect => next with
                {
                    PendingConsumableExhaust = true,
                },
                _ => next,
            };
            return next.PendingConsumableExhaust
                ? next
                : CheckBattleEnd(AppendDefeatLogs(state, next));
        }

        public static BattleState SelectConsumableExhaustCard(
            BattleState state,
            string cardId)
        {
            if (!state.PendingConsumableExhaust)
                return state;

            var hand = state.Player.Hand.ToList();
            var draw = state.Player.DrawPile.ToList();
            var discard = state.Player.DiscardPile.ToList();
            CardDefinition? selected = RemoveFirst(hand, cardId) ??
                RemoveFirst(draw, cardId) ??
                RemoveFirst(discard, cardId);
            if (selected == null)
                return state;
            return state with
            {
                Player = state.Player with
                {
                    Hand = hand,
                    DrawPile = draw,
                    DiscardPile = discard,
                    ExhaustPile = state.Player.ExhaustPile
                        .Concat(new[] { selected }).ToArray(),
                },
                PendingConsumableExhaust = false,
                Log = AddLog(state.Log, $"{selected.Name}を除外した。"),
            };
        }

        private static CardDefinition? RemoveFirst(
            List<CardDefinition> cards,
            string cardId)
        {
            var index = cards.FindIndex(card => card.Id == cardId);
            if (index < 0)
                return null;
            var card = cards[index];
            cards.RemoveAt(index);
            return card;
        }

        private static BattleState ApplyTemporaryMuscle(
            BattleState state,
            int stacks)
        {
            var gained = Math.Max(0, stacks);
            if (gained > 0)
                gained += RelicBonusMuscleOnGain(state);
            var next = ApplyPlayerStatus(
                state, new MuscleStatusEffect(), stacks);
            return next with
            {
                TemporaryStrengthThisTurn =
                    next.TemporaryStrengthThisTurn + gained,
            };
        }

        private static BattleState HealPlayerPercent(
            BattleState state,
            int percent)
        {
            var amount = (int)Math.Ceiling(
                state.Player.MaxHp * Math.Max(0, percent) / 100d);
            return state with
            {
                Player = state.Player with
                {
                    CurrentHp = Math.Min(
                        state.Player.MaxHp,
                        state.Player.CurrentHp + amount),
                },
            };
        }

        private static BattleState GainMaxHpAndHeal(
            BattleState state,
            int maxHp,
            int healPercent)
        {
            var newMaxHp = state.Player.MaxHp + Math.Max(0, maxHp);
            var amount = (int)Math.Ceiling(newMaxHp * Math.Max(0, healPercent) / 100d);
            return state with
            {
                Player = state.Player with
                {
                    MaxHp = newMaxHp,
                    CurrentHp = Math.Min(newMaxHp, state.Player.CurrentHp + amount),
                },
            };
        }

        private static BattleState ClearEnemyBlocks(BattleState state) => state with
        {
            Enemies = state.Enemies
                .Select(enemy => enemy with { Block = 0 })
                .ToArray(),
        };

        private static BattleState ClearPlayerDebuffs(BattleState state)
        {
            var statuses = state.Player.Statuses;
            statuses = SetStatus(statuses, StatusKind.Poison, 0);
            statuses = SetStatus(statuses, StatusKind.Laceration, 0);
            statuses = SetStatus(statuses, StatusKind.Weak, 0);
            return state with { Player = state.Player with { Statuses = statuses } };
        }

        private static AttackTarget PotionAttackTarget(PotionTarget target) =>
            target == PotionTarget.AllEnemies ? AttackTarget.AllEnemies : AttackTarget.Single;

        private static BattleState DoubleTargetPoison(BattleState state, PotionTarget target)
        {
            var indexes = target == PotionTarget.AllEnemies
                ? Enumerable.Range(0, state.Enemies.Count).Where(index => state.Enemies[index].CurrentHp > 0)
                : new[] { DefaultTargetIndex(state) }.Where(index => index >= 0);
            var next = state;
            foreach (var index in indexes)
            {
                var enemy = next.Enemies[index];
                next = ReplaceEnemy(next, index, enemy with
                {
                    Statuses = SetStatus(enemy.Statuses, StatusKind.Poison, Status(enemy.Statuses, StatusKind.Poison) * 2),
                });
            }
            return next;
        }

        private static BattleState ApplyPotionDamage(
            BattleState state,
            int amount,
            AttackTarget target)
        {
            var indexes = target == AttackTarget.AllEnemies
                ? Enumerable.Range(0, state.Enemies.Count)
                    .Where(index => state.Enemies[index].CurrentHp > 0)
                : new[] { DefaultTargetIndex(state) }.Where(index => index >= 0);
            var next = state;
            foreach (var index in indexes)
            {
                var enemy = next.Enemies[index];
                next = DamageEnemy(
                    next,
                    index,
                    ModifiedDamage(
                        amount,
                        next.Player.Statuses,
                        enemy.Statuses),
                    logPlayerDamage: true);
            }
            return next;
        }

        public static BattleState EndPlayerTurn(BattleState state, IRng rng)
        {
            if (state.Phase != BattlePhase.Battle || state.Turn != TurnOwner.Player)
                return state;
            if (state.PendingDiscardCount > 0)
                return state;
            if (state.PendingConsumableExhaust)
                return state;

            // 敵ターンの処理でWeak/Laceration/Dazzledが新たに付与される場合があるため、
            // 「このメソッドが呼ばれた時点（プレイヤーターン終了時）の値」を先に控えておく。
            // DecayStatusesではこの控えた分だけを減衰させ、敵ターン中に新規付与された分は
            // 減衰させずそのまま次ターンへ持ち越す。
            var startingWeak = Status(state.Player.Statuses, StatusKind.Weak);
            var startingLaceration = Status(state.Player.Statuses, StatusKind.Laceration);
            var startingDazzled = Status(state.Player.Statuses, StatusKind.Dazzled);
            var discarded = state.Player.DiscardPile.Concat(state.Player.Hand).ToArray();
            var next = state with
            {
                Player = state.Player with
                {
                    Hand = Array.Empty<CardDefinition>(),
                    DiscardPile = discarded,
                    Statuses = RemoveTemporaryStrength(state.Player.Statuses, state.TemporaryStrengthThisTurn),
                },
                Turn = TurnOwner.Enemy,
                CardsPlayedThisTurn = 0,
                AttackCardsPlayedThisTurn = 0,
                SkillCardsPlayedThisTurn = 0,
                CardsDrawnThisTurn = 0,
                BlockGainsThisTurn = 0,
                NextCardCostReduction = 0,
                NextDefenseCardBonus = 0,
                TemporaryStrengthThisTurn = 0,
                Log = AddLog(
                    state.Log, "プレイヤーがターンを終了した。"),
            };

            for (var index = 0; index < next.Enemies.Count; index++)
            {
                if (next.Enemies[index].CurrentHp <= 0)
                    continue;
                var actingEnemy = next.Enemies[index];
                // 硬化(Hardening)・防具(Armor)は「自分の行動前にブロックへ変換される」常時効果のため、
                // 既存のBlockを上書きする形でここでセットしている（加算ではない点に注意）。
                var hardeningBlock =
                    Status(actingEnemy.Statuses, StatusKind.Hardening) +
                    Status(actingEnemy.Statuses, StatusKind.Armor);
                next = ReplaceEnemy(
                    next, index, actingEnemy with { Block = hardeningBlock });
                next = ExecuteEnemyActionAtIndex(
                    next, index, next.Enemies[index].NextAction, rng);
                if (next.Player.CurrentHp <= 0)
                    break;
            }

            next = ApplyPoison(next);
            next = DecayStatuses(
                next, startingWeak, startingLaceration, startingDazzled);
            next = AppendDefeatLogs(state, next);
            next = CheckBattleEnd(next);
            if (next.Phase != BattlePhase.Battle)
                return next;

            next = RefreshEnemyIntents(next, rng);
            // 通常ブロックはターンをまたぐと失われるのが基本ルールだが、
            // BlockRetentionPercentを持つ場合のみ、その割合分を次ターンへ持ち越す。
            var retention = Math.Max(
                0,
                Math.Max(
                    Status(next.Player.Statuses, StatusKind.BlockRetentionPercent),
                    RelicRetainBlockPercent(next)));
            var retainedBlock = (int)Math.Floor(next.Player.Block * retention / 100d);
            var hardening = Status(next.Player.Statuses, StatusKind.Hardening);
            var armor = Status(next.Player.Statuses, StatusKind.Armor);
            var musclePerTurn = Status(next.Player.Statuses, StatusKind.MusclePerTurn);
            var statuses = SetStatus(
                next.Player.Statuses,
                StatusKind.Muscle,
                Status(next.Player.Statuses, StatusKind.Muscle) + musclePerTurn);
            var prepared = next with
            {
                Turn = TurnOwner.Player,
                Player = next.Player with
                {
                    Energy = next.Player.MaxEnergy + next.PendingNextTurnMaxEnergyBonus,
                    Block = retainedBlock + hardening + armor +
                        HardeningEffectBonus(next),
                    Statuses = statuses,
                },
                PendingNextTurnMaxEnergyBonus = 0,
                PendingNextTurnDrawBonus = 0,
                IsFirstBattleTurn = false,
                PlayerTurnNumber = next.PlayerTurnNumber + 1,
            };
            prepared = ApplyTurnStartRelics(prepared);
            return DrawCards(
                prepared, TurnDrawCount + next.PendingNextTurnDrawBonus, rng);
        }

        // CardEffect の全バリアント（GeneratedUnionTypes.cs で自動生成）を網羅するswitch。
        // 新しい効果種別をデータ側に追加した場合はここにcaseを足す必要があり、
        // 対応漏れは実行時にUnreachableEffectExceptionとして顕在化する（コンパイル時には検出されない）。
        public static BattleState ApplyEffect(
            BattleState state,
            CardEffect effect,
            IRng rng,
            CardDefinition? sourceCard = null) =>
            effect switch
            {
                AttackCardEffect attack => ApplyAttack(
                    state, attack.Amount, attack.Target, OrbDamageMultiplier(sourceCard), sourceCard),
                BlockCardEffect block => AddPlayerBlock(
                    state, block.Amount, OrbBlockMultiplier(sourceCard), sourceCard),
                EnergyScaledAttackCardEffect attack => ApplyAttack(
                    state, state.VariableEnergySpent * attack.Multiplier, AttackTarget.Single,
                    OrbDamageMultiplier(sourceCard), sourceCard),
                EnergyScaledBlockCardEffect block => AddPlayerBlock(
                    state, state.VariableEnergySpent * block.Multiplier,
                    OrbBlockMultiplier(sourceCard), sourceCard),
                EnergyScaledEnemyStatusCardEffect status => ApplyStatusToEnemies(
                    state, status.Status, state.VariableEnergySpent * status.Multiplier, status.Target),
                EnergyScaledPlayerStatusCardEffect status => ApplyPlayerStatus(
                    state, status.Status, state.VariableEnergySpent * status.Multiplier + status.Bonus),
                RunAttackGrowthCardEffect growth => state with
                {
                    RunAttackGrowthBonus = state.RunAttackGrowthBonus + growth.Amount,
                },
                RunBlockGrowthCardEffect growth => state with
                {
                    RunBlockGrowthBonus = state.RunBlockGrowthBonus + growth.Amount,
                },
                DrawCardEffect draw => DrawCards(state, draw.Count, rng),
                GainEnergyCardEffect energy => state with
                {
                    Player = state.Player with { Energy = state.Player.Energy + energy.Amount },
                },
                ApplyStatusCardEffect status => ApplyStatusToEnemies(
                    state, status.Status, status.Stacks, AttackTarget.Single),
                ApplyStatusToEnemiesCardEffect status => ApplyStatusToEnemies(
                    state, status.Status, status.Stacks, status.Target),
                ApplyPlayerStatusCardEffect status => ApplyPlayerStatus(
                    state, status.Status, status.Stacks),
                RemovePlayerStatusCardEffect status => SetPlayerStatus(
                    state,
                    StatusKindOf(status.Status),
                    Math.Max(0, Status(state.Player.Statuses, StatusKindOf(status.Status)) - status.Stacks)),
                MultiplyPlayerStatusCardEffect status => SetPlayerStatus(
                    state,
                    StatusKindOf(status.Status),
                    (int)Math.Floor(Status(state.Player.Statuses, StatusKindOf(status.Status)) * status.Multiplier)),
                MultiAttackCardEffect attack => ApplyMultiAttack(
                    state, attack, OrbDamageMultiplier(sourceCard), sourceCard),
                StatusScaledAttackCardEffect attack => ApplyAttack(
                    state,
                    attack.Amount + Math.Max(
                        0,
                        attack.AmountPerStack - (attack.Status == StatusKind.Muscle ? 1 : 0)) *
                    Status(state.Player.Statuses, attack.Status),
                    AttackTarget.Single,
                    OrbDamageMultiplier(sourceCard),
                    sourceCard),
                ConditionScaledAttackCardEffect attack =>
                    ApplyConditionalAttack(state, attack, sourceCard),
                ConditionScaledBlockCardEffect block =>
                    ApplyConditionalBlock(state, block, sourceCard),
                AttackFromBlockCardEffect attack => ApplyAttack(
                    state, (int)Math.Floor(state.Player.Block * attack.Multiplier),
                    AttackTarget.Single, OrbDamageMultiplier(sourceCard), sourceCard),
                BlockFromTargetStatusCardEffect block => AddPlayerBlock(
                    state, TargetStatus(state, block.Status),
                    OrbBlockMultiplier(sourceCard), sourceCard),
                MultiplyEnemyStatusCardEffect status => MultiplyTargetStatus(
                    state, status.Status, status.Multiplier),
                SpreadEnemyStatusCardEffect status => SpreadTargetStatus(
                    state, status.Status, status.Multiplier),
                SelfDamageCardEffect damage => state with
                {
                    Player = state.Player with
                    {
                        CurrentHp = Math.Max(0, state.Player.CurrentHp - damage.Amount),
                    },
                },
                DiscardCardEffect discard => state with
                {
                    PendingDiscardCount =
                        state.PendingDiscardCount + Math.Max(0, discard.Count),
                },
                ConditionalAttackCardEffect attack => ApplyAttack(
                    state,
                    attack.BaseAmount + (state.AttackCardsPlayedThisTurn > 0 ? attack.BonusAmount : 0),
                    attack.Target,
                    OrbDamageMultiplier(sourceCard), sourceCard),
                CostReductionNextCardCardEffect reduction => state with
                {
                    NextCardCostReduction = state.NextCardCostReduction + reduction.Amount,
                },
                BuffNextDefenseCardEffect buff => state with
                {
                    NextDefenseCardBonus = state.NextDefenseCardBonus + buff.Amount,
                },
                AmplifyEnemyStatusCardEffect amplify => AmplifyTargetDebuffs(state, amplify.Amount),
                SetPlayerHpToCardEffect hp => state with
                {
                    Player = state.Player with
                    {
                        CurrentHp = Math.Min(state.Player.CurrentHp, hp.Amount),
                    },
                },
                HpPercentAttackAndSelfDamageCardEffect attack =>
                    ApplyHpPercentAttackAndSelfDamage(
                        state, attack.Percent, OrbDamageMultiplier(sourceCard), sourceCard),
                BattleScaledAttackCardEffect attack => ApplyBattleScaledAttack(
                    state, attack, OrbDamageMultiplier(sourceCard), sourceCard),
                NextTurnMaxEnergyBonusCardEffect bonus => state with
                {
                    PendingNextTurnMaxEnergyBonus =
                        state.PendingNextTurnMaxEnergyBonus + bonus.Amount,
                },
                NextTurnDrawBonusCardEffect bonus => state with
                {
                    PendingNextTurnDrawBonus = state.PendingNextTurnDrawBonus + bonus.Amount,
                },
                DrawAndZeroCostIfAttackCardEffect draw =>
                    DrawAndZeroCostAttack(state, draw.Count, rng),
                _ => throw new UnreachableEffectException(effect.GetType()),
            };

        private static BattleState ResolveForcedDiscard(BattleState state)
        {
            if (state.Phase != BattlePhase.Battle ||
                state.PendingDiscardCount <= 0 ||
                state.Player.Hand.Count > state.PendingDiscardCount)
                return state;

            var discarded = state.Player.Hand.ToArray();
            return state with
            {
                Player = state.Player with
                {
                    Hand = Array.Empty<CardDefinition>(),
                    DiscardPile = state.Player.DiscardPile
                        .Concat(discarded).ToArray(),
                },
                PendingDiscardCount = 0,
                Log = discarded.Aggregate(
                    state.Log,
                    (log, card) => AddLog(
                        log, $"プレイヤーが{card.Name}を捨てた。")),
            };
        }

        public static BattleState ExecuteEnemyAction(
            BattleState state,
            string enemyInstanceId,
            EnemyAction action,
            IRng rng)
        {
            var index = state.Enemies.ToList().FindIndex(
                enemy => enemy.InstanceId == enemyInstanceId);
            return index < 0
                ? state
                : ExecuteEnemyActionAtIndex(state, index, action, rng);
        }

        private static BattleState ExecuteEnemyActionAtIndex(
            BattleState state,
            int enemyIndex,
            EnemyAction action,
            IRng rng) =>
            action switch
            {
                AttackEnemyAction attack => ApplyEnemyAttack(state, enemyIndex, attack.Amount),
                BlockEnemyAction block => AddEnemyBlock(state, enemyIndex, block.Amount),
                MultiAttackEnemyAction multi => ApplyEnemyMultiAttack(state, enemyIndex, multi),
                AttackAndApplyStatusEnemyAction attack => ApplyEnemyStatusAction(
                    ApplyEnemyAttack(state, enemyIndex, attack.Amount),
                    enemyIndex, attack.Target, attack.Status, attack.Stacks),
                ApplyStatusEnemyAction status => ApplyEnemyStatusAction(
                    state, enemyIndex, status.Target, status.Status, status.Stacks),
                ApplyStatusesEnemyAction statuses => statuses.Statuses.Aggregate(
                    state,
                    (current, status) => ApplyEnemyStatusAction(
                        current, enemyIndex, statuses.Target, status.Status, status.Stacks)),
                BuffEnemyAction buff => ApplyEnemyStatusAction(
                    state, enemyIndex, EnemyStatusTarget.Self, buff.Status, buff.Stacks),
                OmenEnemyAction omen => state with
                {
                    Log = AddLog(state.Log, omen.Description),
                },
                BlockAndAttackEnemyAction both => ApplyEnemyAttack(
                    AddEnemyBlock(state, enemyIndex, both.BlockAmount),
                    enemyIndex,
                    both.AttackAmount),
                SummonEnemyAction summon =>
                    SummonEnemy(state, enemyIndex, summon.EnemyId, rng),
                IdleEnemyAction => state,
                _ => throw new UnreachableEffectException(action.GetType()),
            };

        private static BattleState ApplyEnemyStatusAction(
            BattleState state,
            int enemyIndex,
            EnemyStatusTarget target,
            StatusEffect effect,
            int stacks)
        {
            var kind = StatusKindOf(effect);
            if (target == EnemyStatusTarget.Player)
                return SetPlayerStatus(
                    state,
                    kind,
                    kind == StatusKind.Laceration
                        ? Math.Min(3, Status(state.Player.Statuses, kind) + stacks)
                        : Status(state.Player.Statuses, kind) + stacks);

            var enemy = state.Enemies[enemyIndex];
            return ReplaceEnemy(state, enemyIndex, enemy with
            {
                Statuses = SetStatus(
                    enemy.Statuses,
                    kind,
                    kind == StatusKind.Laceration
                        ? Math.Min(3, Status(enemy.Statuses, kind) + stacks)
                        : Status(enemy.Statuses, kind) + stacks),
            });
        }

        private static BattleState SummonEnemy(
            BattleState state,
            int summonerIndex,
            string enemyId,
            IRng rng)
        {
            var suffix = state.Enemies.Count(enemy => enemy.EnemyId == enemyId) + 1;
            var summoned = EnemyFactory.Create(
                enemyId,
                state.Enemies[summonerIndex].TrialLevel,
                $"{enemyId}-summoned-{suffix}",
                rng);
            return state with
            {
                Enemies = state.Enemies.Concat(new[] { summoned }).ToArray(),
            };
        }

        private static BattleState RefreshEnemyIntents(BattleState state, IRng rng)
        {
            var next = state;
            for (var index = 0; index < next.Enemies.Count; index++)
            {
                var enemy = next.Enemies[index];
                if (enemy.CurrentHp <= 0)
                    continue;
                var turn = enemy.BattleTurn + 1;
                var action = string.IsNullOrEmpty(enemy.BehaviorId)
                    ? enemy.NextAction
                    : EnemyBehaviorRegistry.Resolve(enemy.BehaviorId, enemy.TrialLevel)
                        .SelectAction(
                            new EnemyActionContext(
                                turn, enemy.CurrentHp, next.Player.CurrentHp),
                            rng);
                next = ReplaceEnemy(next, index, enemy with
                {
                    BattleTurn = turn,
                    NextAction = action,
                });
            }
            return next;
        }

        private static int EffectiveCost(BattleState state, CardDefinition card)
        {
            // オーブ効果によるコスト0化は他の割引より優先する
            // （Variable/Zero/固定コスト＋各種割引の判定より先に確定させる）。
            if (card.SpecialOrbSlot is FilledSpecialOrbSlot
                {
                    Effect: SetCostZeroSpecialOrbEffect
                })
                return 0;
            if (card.Category == CardCategory.Skill &&
                state.SkillCardsPlayedThisTurn == 0)
            {
                var firstSkillCost = FirstSkillCardCost(state);
                if (firstSkillCost.HasValue)
                    return firstSkillCost.Value;
            }
            if (card.Cost is VariableCardCost)
                return state.Player.Energy;
            if (card.Cost is ZeroCardCost)
                return 0;
            var fixedCost = ((FixedCardCost)card.Cost).Energy;
            var conditionalReduction = card.CostReductionCondition != null &&
                state.CardsDrawnThisTurn >= card.CostReductionCondition.Count
                ? card.CostReductionCondition.Reduction
                : 0;
            return Math.Max(0, fixedCost - state.NextCardCostReduction - conditionalReduction);
        }

        private static int? FirstSkillCardCost(BattleState state)
        {
            int? result = null;
            foreach (var relic in state.Relics)
            {
                var candidate = relic.Effect switch
                {
                    FirstSkillCardFreeRelicEffect => 0,
                    FirstSkillCardCostRelicEffect cost => Math.Max(0, cost.Amount),
                    _ => (int?)null,
                };
                if (!candidate.HasValue)
                    continue;
                result = !result.HasValue
                    ? candidate.Value
                    : Math.Min(result.Value, candidate.Value);
            }
            return result;
        }

        private static BattleState ApplySpecialOrbPostPlay(
            BattleState state,
            CardDefinition card,
            IRng rng)
        {
            if (card.SpecialOrbSlot is not FilledSpecialOrbSlot filled)
                return state;
            return filled.Effect switch
            {
                DrawCardsSpecialOrbEffect draw => DrawCards(state, draw.Count, rng),
                HealSpecialOrbEffect heal => state with
                {
                    Player = state.Player with
                    {
                        CurrentHp = Math.Min(
                            state.Player.MaxHp,
                            state.Player.CurrentHp + heal.Amount),
                    },
                },
                GainMuscleSpecialOrbEffect muscle => ApplyPlayerStatus(
                    state, new MuscleStatusEffect(), muscle.Stacks),
                GainArmorSpecialOrbEffect armor => ApplyPlayerStatus(
                    state, new ArmorStatusEffect(), armor.Stacks),
                _ => state,
            };
        }

        private static int OrbDamageMultiplier(CardDefinition? card) =>
            card?.SpecialOrbSlot is FilledSpecialOrbSlot
            {
                Effect: DoubleDamageSpecialOrbEffect
            } ? 2 : 1;

        private static int OrbBlockMultiplier(CardDefinition? card) =>
            card?.SpecialOrbSlot is FilledSpecialOrbSlot
            {
                Effect: DoubleBlockSpecialOrbEffect
            } ? 2 : 1;

        private static BattleState ApplyAttack(
            BattleState state,
            int amount,
            AttackTarget target,
            int orbMultiplier = 1,
            CardDefinition? sourceCard = null)
        {
            var indexes = target == AttackTarget.AllEnemies
                ? Enumerable.Range(0, state.Enemies.Count)
                    .Where(index => state.Enemies[index].CurrentHp > 0)
                : new[] { DefaultTargetIndex(state) }.Where(index => index >= 0);
            var next = state;
            var affectedEnemyCount = 0;
            foreach (var index in indexes)
            {
                var multiplier = FirstAttackMultiplierPercent(next);
                var relicBonus = RelicPassiveAttackBonus(next, sourceCard);
                var scaledAmount = (int)Math.Floor(
                    (amount + next.RunAttackGrowthBonus + relicBonus) *
                    multiplier / 100d);
                scaledAmount = ApplyRelicAttackPercentBonus(
                    next,
                    scaledAmount,
                    next.Enemies[index],
                    sourceCard);
                next = DamageEnemy(next, index, ModifiedDamage(
                    scaledAmount * orbMultiplier,
                    next.Player.Statuses,
                    next.Enemies[index].Statuses),
                    logPlayerDamage: true);
                affectedEnemyCount++;
            }
            return target == AttackTarget.AllEnemies && affectedEnemyCount > 0
                ? next with
                {
                    Log = AddLog(next.Log, $"敵全体に{amount}ダメージを与えた。"),
                }
                : next;
        }

        private static BattleState DamageEnemy(
            BattleState state,
            int index,
            int damage,
            bool logPlayerDamage = false)
        {
            var enemy = state.Enemies[index];
            var hpDamage = Math.Max(0, damage - enemy.Block);
            var updated = enemy with
            {
                Block = Math.Max(0, enemy.Block - damage),
                CurrentHp = Math.Max(0, enemy.CurrentHp - hpDamage),
            };
            var next = ReplaceEnemy(state, index, updated);
            return logPlayerDamage
                ? next with
                {
                    Log = AddLog(
                        next.Log,
                        $"プレイヤーが{damage}ダメージを与えた。" +
                        $"{enemy.Name}HP: {updated.CurrentHp}"),
                }
                : next;
        }

        private static BattleState ApplyEnemyAttack(BattleState state, int index, int amount)
        {
            var enemy = state.Enemies[index];
            var damage = ModifiedDamage(amount, enemy.Statuses, state.Player.Statuses);
            var percentReduction = state.Relics.Sum(relic =>
                relic.Effect is DamageReductionFromLaceratedEnemyRelicEffect effect &&
                Status(enemy.Statuses, StatusKind.Laceration) > 0
                    ? effect.Percent
                    : 0);
            damage = (int)Math.Floor(
                damage * Math.Max(0, 100 - percentReduction) / 100d);
            var flatReduction = state.Relics.Sum(relic =>
                relic.Effect is DamageReductionFromPoisonedEnemyRelicEffect effect &&
                Status(enemy.Statuses, StatusKind.Poison) > 0
                    ? effect.Amount
                    : 0);
            damage = Math.Max(0, damage - flatReduction);
            // DamageNegateReflect（無効化して反射）は通常のDamageReflect（受けつつ反射）より優先する。
            // 同じ攻撃で両方のステータスを持っていても、プレイヤーはダメージを受けない側が適用される。
            var negateReflect = Status(
                state.Player.Statuses, StatusKind.DamageNegateReflect);
            if (negateReflect > 0)
            {
                var reflected = DamageEnemy(state, index, damage);
                return SetPlayerStatus(
                    reflected,
                    StatusKind.DamageNegateReflect,
                    negateReflect - 1);
            }

            var hpDamage = Math.Max(0, damage - state.Player.Block);
            var damaged = state with
            {
                Player = state.Player with
                {
                    Block = Math.Max(0, state.Player.Block - damage),
                    CurrentHp = Math.Max(0, state.Player.CurrentHp - hpDamage),
                },
            };
            damaged = damaged with
            {
                Log = AddLog(
                    damaged.Log,
                    $"{enemy.Name}が{damage}ダメージを与えた。" +
                    $"プレイヤーHP: {damaged.Player.CurrentHp}"),
            };
            var reflect = Status(state.Player.Statuses, StatusKind.DamageReflect);
            if (reflect <= 0)
                return damaged;
            return SetPlayerStatus(
                DamageEnemy(damaged, index, damage),
                StatusKind.DamageReflect,
                reflect - 1);
        }

        private static BattleState ApplyEnemyMultiAttack(
            BattleState state,
            int enemyIndex,
            MultiAttackEnemyAction action)
        {
            var next = state;
            for (var hit = 0; hit < action.Times && next.Player.CurrentHp > 0; hit++)
                next = ApplyEnemyAttack(next, enemyIndex, action.Amount);
            return next;
        }

        // 攻撃側の筋力(Muscle)・弱体(Weak)を先に加減算して0未満にならないよう丸めた後、
        // 防御側の裂傷ボーナスを加算する（裂傷は弱体化の影響を受けない別枠加算のため、
        // Math.Maxの外に置いている）。
        private static int ModifiedDamage(
            int amount,
            IReadOnlyDictionary<StatusKind, int> attacker,
            IReadOnlyDictionary<StatusKind, int> defender) =>
            Math.Max(0, amount + Status(attacker, StatusKind.Muscle) -
                Status(attacker, StatusKind.Weak)) +
            LacerationBonus(Status(defender, StatusKind.Laceration));

        // 裂傷(Laceration)は3スタックで頭打ちの段階ボーナス（1→5, 2→10, 3→20固定ダメージ加算）。
        // スタック数に比例させず、あえて非線形なテーブルにしているのはバランス調整のため
        // （4スタック以上蓄積しても3スタック分の+20から増えない）。
        private static int LacerationBonus(int stacks) =>
            Math.Min(3, Math.Max(0, stacks)) switch
            {
                1 => 5,
                2 => 10,
                3 => 20,
                _ => 0,
            };

        private static BattleState AddPlayerBlock(
            BattleState state,
            int amount,
            int orbMultiplier = 1,
            CardDefinition? sourceCard = null)
        {
            var relicBonus = state.Relics.Sum(relic => relic.Effect switch
            {
                FirstBlockGainBonusRelicEffect effect when
                    state.BlockGainsThisTurn == 0 => effect.Amount,
                BlockBonusWhileHardeningRelicEffect effect when
                    Status(state.Player.Statuses, StatusKind.Hardening) > 0 => effect.Amount,
                LowCostBlockBonusRelicEffect effect when
                    sourceCard?.Cost is FixedCardCost cost &&
                    cost.Energy <= effect.MaximumCost => effect.Amount,
                _ => 0,
            });
            var gained = Math.Max(
                0,
                (amount + state.RunBlockGrowthBonus) * orbMultiplier +
                state.NextDefenseCardBonus +
                Status(state.Player.Statuses, StatusKind.Armor) +
                relicBonus);
            var block = state.Player.Block + gained;
            return state with
            {
                Player = state.Player with { Block = block },
                NextDefenseCardBonus = 0,
                BlockGainsThisTurn = state.BlockGainsThisTurn +
                    (gained > 0 ? 1 : 0),
                Log = AddLog(
                    state.Log,
                    $"プレイヤーが{gained}ブロックを得た。ブロック: {block}"),
            };
        }

        private static BattleState ApplyMultiAttack(
            BattleState state,
            MultiAttackCardEffect attack,
            int orbMultiplier = 1,
            CardDefinition? sourceCard = null)
        {
            var next = state;
            var singleTargetIndex = attack.Target == AttackTarget.Single
                ? DefaultTargetIndex(state)
                : -1;
            for (var hit = 0; hit < attack.Times; hit++)
            {
                var indexes = attack.Target == AttackTarget.Single
                    ? new[] { singleTargetIndex }.Where(index => index >= 0)
                    : Enumerable.Range(0, next.Enemies.Count)
                        .Where(index => next.Enemies[index].CurrentHp > 0);
                foreach (var index in indexes)
                {
                    if (next.Enemies[index].CurrentHp <= 0)
                        continue;
                    var enemy = next.Enemies[index];
                    var relicBonus = RelicPassiveAttackBonus(next, sourceCard) +
                        RelicMultiAttackEachHitBonus(next) +
                        (hit > 0 && next.IsFirstBattleTurn
                            ? RelicMultiAttackFollowUpBonus(next)
                            : 0) +
                        (hit == attack.Times - 1
                            ? RelicMultiAttackFinalHitBonus(next, attack.Times)
                            : 0);
                    var firstAttackMultiplier = FirstAttackMultiplierPercent(next);
                    var scaledAmount = (int)Math.Floor(
                        (attack.Amount + next.RunAttackGrowthBonus + relicBonus) *
                        firstAttackMultiplier / 100d);
                    scaledAmount = ApplyRelicAttackPercentBonus(
                        next, scaledAmount, enemy, sourceCard);
                    next = DamageEnemy(
                        next,
                        index,
                        ModifiedDamage(
                            scaledAmount * orbMultiplier,
                            next.Player.Statuses,
                            enemy.Statuses),
                        logPlayerDamage: true);
                    if (attack.Target == AttackTarget.Single &&
                        next.Enemies[index].CurrentHp <= 0)
                        break;
                }
                if (attack.Target == AttackTarget.Single &&
                    (singleTargetIndex < 0 ||
                     next.Enemies[singleTargetIndex].CurrentHp <= 0))
                    break;
            }
            return next;
        }

        private static int RelicPassiveAttackBonus(
            BattleState state,
            CardDefinition? sourceCard)
        {
            var isAttackCard = sourceCard?.Category == CardCategory.Attack;
            var bonus = state.Relics.Sum(relic => relic.Effect switch
            {
                AttackDamageBonusRelicEffect effect when isAttackCard => effect.Amount,
                HighCostAttackDamageBonusRelicEffect effect when
                    isAttackCard &&
                    sourceCard?.Cost is FixedCardCost cost &&
                    cost.Energy >= effect.MinCost => effect.Amount,
                UpgradedCardDamageBonusRelicEffect effect when
                    isAttackCard &&
                    sourceCard?.Upgraded == true => effect.Amount,
                FirstAttackCardDamageBonusRelicEffect effect when
                    isAttackCard &&
                    state.AttackCardsPlayedThisTurn == 0 => effect.Amount,
                FirstTurnFirstAttackBonusRelicEffect effect when
                    isAttackCard &&
                    state.IsFirstBattleTurn &&
                    state.AttackCardsPlayedThisTurn == 0 => effect.Amount,
                SingleAttackDamageBonusRelicEffect effect when
                    sourceCard?.Category == CardCategory.Attack &&
                    sourceCard.Effects.All(item => item is not MultiAttackCardEffect) =>
                    effect.Amount,
                _ => 0,
            });
            return bonus;
        }

        private static int RelicAttackPercentBonus(
            BattleState state,
            EnemyCombatState enemy,
            CardDefinition? sourceCard)
        {
            if (sourceCard?.Category != CardCategory.Attack)
                return 0;
            var muscle = Status(state.Player.Statuses, StatusKind.Muscle);
            return state.Relics.Sum(relic => relic.Effect switch
            {
                AttackPercentBonusRelicEffect effect => effect.Percent,
                AttackPercentIfMuscleRelicEffect effect when muscle >= effect.Minimum =>
                    effect.Percent,
                AttackPercentVsStatusRelicEffect effect when
                    Status(enemy.Statuses, effect.Status) > 0 => effect.Percent,
                AttackPercentAtLowHpRelicEffect effect when
                    state.Player.CurrentHp * 100 <=
                    state.Player.MaxHp * effect.ThresholdPercent => effect.Percent,
                _ => 0,
            });
        }

        private static int ApplyRelicAttackPercentBonus(
            BattleState state,
            int amount,
            EnemyCombatState enemy,
            CardDefinition? sourceCard)
        {
            var percent = RelicAttackPercentBonus(state, enemy, sourceCard);
            if (percent == 0)
                return amount;
            var muscle = Status(state.Player.Statuses, StatusKind.Muscle);
            return (int)Math.Floor(
                (amount + muscle) * (1 + percent / 100d)) - muscle;
        }

        private static int RelicMultiAttackEachHitBonus(BattleState state) =>
            state.Relics.Sum(relic => relic.Effect is MultiAttackEachHitBonusRelicEffect effect
                ? effect.Amount
                : 0);

        private static int RelicMultiAttackFollowUpBonus(BattleState state) =>
            state.Relics.Sum(relic => relic.Effect is FirstTurnMultiAttackFollowUpBonusRelicEffect effect
                ? effect.Amount
                : 0);

        private static int RelicMultiAttackFinalHitBonus(
            BattleState state,
            int hitCount) =>
            state.Relics.Sum(relic => relic.Effect is MultiAttackFinalHitBonusRelicEffect effect &&
                hitCount >= effect.MinimumHits
                ? effect.Amount
                : 0);

        private static int RelicRetainBlockPercent(BattleState state) =>
            state.Relics.Sum(relic => relic.Effect is RetainBlockPercentRelicEffect effect
                ? effect.Percent
                : 0);

        // このターン最初の攻撃カードにのみ倍率がかかる（2枚目以降は常に100%）。
        // Math.Max(100, ...)としているのは、ステータス値が未設定(0)のときに
        // 倍率が0%扱いになって攻撃力が消えてしまう事故を防ぐため
        // （このステータスは「強化」専用で、100%を下回る調整には使わない設計）。
        private static int FirstAttackMultiplierPercent(BattleState state) =>
            state.AttackCardsPlayedThisTurn == 0
                ? Math.Max(
                    100,
                    Status(
                        state.Player.Statuses,
                        StatusKind.FirstAttackMultiplierPercent))
                : 100;

        private static BattleState ApplyStatusToEnemies(
            BattleState state,
            StatusEffect status,
            int stacks,
            AttackTarget target,
            bool includeRelicApplicationBonus = true,
            int? targetIndex = null)
        {
            var kind = StatusKindOf(status);
            var indexes = targetIndex.HasValue
                ? new[] { targetIndex.Value }.Where(index =>
                    index >= 0 && index < state.Enemies.Count)
                : target == AttackTarget.AllEnemies
                ? Enumerable.Range(0, state.Enemies.Count)
                    .Where(index => state.Enemies[index].CurrentHp > 0)
                : new[] { DefaultTargetIndex(state) }.Where(index => index >= 0);
            var applicationBonus = kind == StatusKind.Poison
                ? Status(state.Player.Statuses, StatusKind.PoisonApplicationBonus)
                : 0;
            if (includeRelicApplicationBonus && kind == StatusKind.Poison)
                applicationBonus += state.Relics.Sum(relic =>
                    relic.Effect is BonusPoisonOnApplyRelicEffect effect
                        ? effect.Bonus
                        : 0);
            if (includeRelicApplicationBonus && kind == StatusKind.Laceration)
                applicationBonus += state.Relics.Sum(relic =>
                    relic.Effect is BonusLacerationOnApplyRelicEffect effect
                        ? effect.Bonus
                        : 0);
            var effectiveStacks = Math.Max(0, stacks);
            if (effectiveStacks > 0)
                effectiveStacks += applicationBonus;
            var next = state;
            var affectedEnemyCount = 0;
            foreach (var index in indexes)
            {
                var enemy = next.Enemies[index];
                next = ReplaceEnemy(next, index, enemy with
                {
                    // 裂傷は段階テーブルが3スタックまでしか定義されていないため、
                    // 付与元が複数あっても上限を越えないようにする。
                    Statuses = SetStatus(
                        enemy.Statuses,
                        kind,
                        kind == StatusKind.Laceration
                            ? Math.Min(3, Status(enemy.Statuses, kind) + effectiveStacks)
                            : Status(enemy.Statuses, kind) + effectiveStacks),
                });
                affectedEnemyCount++;
            }
            return target == AttackTarget.AllEnemies && !targetIndex.HasValue &&
                affectedEnemyCount > 0 && effectiveStacks > 0
                ? next with
                {
                    Log = AddLog(
                        next.Log,
                        $"敵全体に{StatusDisplayName(kind)}{effectiveStacks}を付与した。"),
                }
                : next;
        }

        private static BattleState ApplyPlayerStatus(
            BattleState state,
            StatusEffect status,
            int stacks)
        {
            var kind = StatusKindOf(status);
            var bonus = stacks > 0 && kind == StatusKind.Muscle
                ? RelicBonusMuscleOnGain(state)
                : 0;
            return SetPlayerStatus(
                state,
                kind,
                Status(state.Player.Statuses, kind) + stacks + bonus);
        }

        private static BattleState SetPlayerStatus(BattleState state, StatusKind kind, int value) =>
            state with
            {
                Player = state.Player with
                {
                    Statuses = SetStatus(state.Player.Statuses, kind, value),
                },
            };

        private static BattleState ApplyConditionalAttack(
            BattleState state,
            ConditionScaledAttackCardEffect effect,
            CardDefinition? sourceCard)
        {
            var met = ConditionMet(state, effect.Condition, sourceCard);
            if (effect.Mode == ScaledEffectMode.Replace)
                return ApplyAttack(
                    state,
                    met ? effect.ConditionalAmount : effect.Amount,
                    AttackTarget.Single,
                    OrbDamageMultiplier(sourceCard),
                    sourceCard);
            var next = ApplyAttack(
                state, effect.Amount, AttackTarget.Single,
                OrbDamageMultiplier(sourceCard),
                sourceCard);
            return met
                ? ApplyAttack(
                    next, effect.ConditionalAmount, AttackTarget.Single,
                    OrbDamageMultiplier(sourceCard),
                    sourceCard)
                : next;
        }

        private static BattleState ApplyConditionalBlock(
            BattleState state,
            ConditionScaledBlockCardEffect effect,
            CardDefinition? sourceCard)
        {
            var met = ConditionMet(state, effect.Condition, sourceCard);
            if (effect.Mode == ScaledEffectMode.Replace)
                return AddPlayerBlock(
                    state,
                    met ? effect.ConditionalAmount : effect.Amount,
                    OrbBlockMultiplier(sourceCard),
                    sourceCard);
            var next = AddPlayerBlock(
                state, effect.Amount, OrbBlockMultiplier(sourceCard), sourceCard);
            return met
                ? AddPlayerBlock(
                    next,
                    effect.ConditionalAmount,
                    OrbBlockMultiplier(sourceCard),
                    sourceCard)
                : next;
        }

        private static bool ConditionMet(
            BattleState state,
            ScaledEffectCondition condition,
            CardDefinition? sourceCard) =>
            condition switch
            {
                PlayerHasBlockCondition => state.Player.Block > 0,
                TargetHasStatusCondition target =>
                    TargetStatus(state, target.Status) >= target.MinimumStacks,
                PlayerHpAtOrBelowPercentCondition hp =>
                    state.Player.CurrentHp * 100 <= state.Player.MaxHp * hp.Percent,
                CardsDrawnThisTurnAtLeastCondition drawn =>
                    state.CardsDrawnThisTurn >= drawn.Count,
                CardHasFilledSpecialOrbCondition =>
                    sourceCard?.SpecialOrbSlot is FilledSpecialOrbSlot,
                _ => throw new UnreachableEffectException(condition.GetType()),
            };

        private static int TargetStatus(BattleState state, StatusKind kind)
        {
            var index = DefaultTargetIndex(state);
            return index < 0 ? 0 : Status(state.Enemies[index].Statuses, kind);
        }

        private static BattleState MultiplyTargetStatus(
            BattleState state,
            StatusKind kind,
            double multiplier)
        {
            var index = DefaultTargetIndex(state);
            if (index < 0)
                return state;
            var enemy = state.Enemies[index];
            return ReplaceEnemy(state, index, enemy with
            {
                Statuses = SetStatus(
                    enemy.Statuses,
                    kind,
                    (int)Math.Floor(Status(enemy.Statuses, kind) * multiplier)),
            });
        }

        private static BattleState SpreadTargetStatus(
            BattleState state,
            StatusKind kind,
            double multiplier)
        {
            var sourceIndex = DefaultTargetIndex(state);
            if (sourceIndex < 0)
                return state;
            var stacks = (int)Math.Floor(
                Status(state.Enemies[sourceIndex].Statuses, kind) * multiplier);
            var next = state;
            for (var index = 0; index < state.Enemies.Count; index++)
            {
                if (index == sourceIndex || state.Enemies[index].CurrentHp <= 0)
                    continue;
                var enemy = next.Enemies[index];
                next = ReplaceEnemy(next, index, enemy with
                {
                    Statuses = SetStatus(
                        enemy.Statuses,
                        kind,
                        Status(enemy.Statuses, kind) + stacks),
                });
            }
            return next;
        }

        private static BattleState AmplifyTargetDebuffs(BattleState state, int amount)
        {
            var index = DefaultTargetIndex(state);
            if (index < 0)
                return state;
            var enemy = state.Enemies[index];
            var statuses = enemy.Statuses;
            // 「増幅」は既に付与済みのデバフのみを底上げする効果であり、0スタックの状態から
            // 新規付与はしない（poison/laceration > 0 のガードはそのため）。
            // 裂傷は LacerationBonus の段階テーブルに合わせて3スタックで頭打ちにする。
            var poison = Status(statuses, StatusKind.Poison);
            var laceration = Status(statuses, StatusKind.Laceration);
            if (poison > 0)
                statuses = SetStatus(statuses, StatusKind.Poison, poison + amount);
            if (laceration > 0)
                statuses = SetStatus(
                    statuses,
                    StatusKind.Laceration,
                    Math.Min(3, laceration + amount));
            return ReplaceEnemy(state, index, enemy with { Statuses = statuses });
        }

        private static BattleState ApplyHpPercentAttackAndSelfDamage(
            BattleState state,
            int percent,
            int orbMultiplier = 1,
            CardDefinition? sourceCard = null)
        {
            var amount = (int)Math.Floor(state.Player.CurrentHp * percent / 100d);
            var next = ApplyAttack(
                state, amount, AttackTarget.Single, orbMultiplier, sourceCard);
            return next with
            {
                Player = next.Player with
                {
                    CurrentHp = Math.Max(0, next.Player.CurrentHp - amount),
                },
            };
        }

        private static BattleState ApplyBattleScaledAttack(
            BattleState state,
            BattleScaledAttackCardEffect effect,
            int orbMultiplier = 1,
            CardDefinition? sourceCard = null)
        {
            var next = ApplyAttack(
                state,
                effect.BaseAmount + effect.GrowthPerUse * state.LoopAttackUsesThisBattle,
                AttackTarget.Single,
                orbMultiplier,
                sourceCard);
            return next with
            {
                LoopAttackUsesThisBattle = state.LoopAttackUsesThisBattle + 1,
            };
        }

        private static BattleState AddEnemyBlock(BattleState state, int index, int amount)
        {
            var enemy = state.Enemies[index];
            var gained = Math.Max(0, amount + Status(enemy.Statuses, StatusKind.Armor));
            return ReplaceEnemy(state, index, enemy with { Block = enemy.Block + gained });
        }

        private static BattleState DrawCards(BattleState state, int count, IRng rng)
        {
            var hand = new List<CardDefinition>(state.Player.Hand);
            var draw = new List<CardDefinition>(state.Player.DrawPile);
            var discard = new List<CardDefinition>(state.Player.DiscardPile);
            var overflow = new List<CardDefinition>();
            var drawn = 0;
            while (drawn < count)
            {
                if (draw.Count == 0)
                {
                    // 山札が尽きたら捨て札をシャッフルして山札に戻す（一般的なデッキ構築ローグライクの挙動）。
                    // 捨て札も空なら、それ以上引けないので指定枚数に届かなくても打ち切る。
                    if (discard.Count == 0)
                        break;
                    draw.AddRange(rng.Shuffled(discard));
                    discard.Clear();
                }
                var card = draw[0];
                // 幻惑(Dazzled)状態の間は、固定コストカードを引くたびにコストが1〜3のランダム値に
                // 上書きされる（デッキ内の元のカード定義は変えず、手札に入った個体だけを差し替える）。
                if (Status(state.Player.Statuses, StatusKind.Dazzled) > 0 &&
                    card.Cost is FixedCardCost)
                    card = card with
                    {
                        Cost = new FixedCardCost(rng.NextIntInclusive(1, 3)),
                    };
                if (hand.Count < MaxHandSize)
                    hand.Add(card);
                else
                    overflow.Add(card);
                draw.RemoveAt(0);
                drawn++;
            }
            if (overflow.Count > 0)
                discard.AddRange(overflow);
            return state with
            {
                Player = state.Player with
                {
                    Hand = hand,
                    DrawPile = draw,
                    DiscardPile = discard,
                },
                CardsDrawnThisTurn = state.CardsDrawnThisTurn + drawn,
                Log = overflow.Count == 0
                    ? state.Log
                    : AddLog(
                        state.Log,
                        $"手札上限のため、{overflow.Count}枚を捨て札に送った。"),
            };
        }

        private static BattleState DrawAndZeroCostAttack(
            BattleState state,
            int count,
            IRng rng)
        {
            // 新たに引いたカードだけをコスト0化したいので、引く前の手札枚数を基準に
            // 「index以降が今回のドローで追加された分」と判定する
            // （引く前から手札にあった攻撃カードは対象外）。
            var originalHandCount = state.Player.Hand.Count;
            var drawn = DrawCards(state, count, rng);
            var hand = drawn.Player.Hand.Select((card, index) =>
                index >= originalHandCount &&
                card.Category == CardCategory.Attack &&
                card.Cost is FixedCardCost
                    ? card with { Cost = new ZeroCardCost() }
                    : card).ToArray();
            return drawn with { Player = drawn.Player with { Hand = hand } };
        }

        private static BattleState ApplyPoison(BattleState state)
        {
            var playerPoison = Status(state.Player.Statuses, StatusKind.Poison);
            var next = state;
            if (playerPoison > 0)
                next = next with
                {
                    Player = next.Player with
                    {
                        CurrentHp = Math.Max(0, next.Player.CurrentHp - playerPoison),
                        Statuses = SetStatus(next.Player.Statuses, StatusKind.Poison, playerPoison - 1),
                    },
                };

            for (var index = 0; index < next.Enemies.Count; index++)
            {
                var poison = Status(next.Enemies[index].Statuses, StatusKind.Poison);
                if (poison <= 0)
                    continue;
                var enemy = next.Enemies[index];
                var killed = enemy.CurrentHp > 0 && enemy.CurrentHp <= poison;
                next = ReplaceEnemy(next, index, enemy with
                {
                    CurrentHp = Math.Max(0, enemy.CurrentHp - poison),
                    Statuses = SetStatus(enemy.Statuses, StatusKind.Poison, poison - 1),
                });
                if (killed)
                {
                    var poisonKills = new HashSet<string>(next.PoisonKilledEnemyIds)
                    {
                        enemy.InstanceId,
                    };
                    next = next with { PoisonKilledEnemyIds = poisonKills };
                }
            }
            return next;
        }

        private static BattleState DecayStatuses(
            BattleState state,
            int startingWeak,
            int startingLaceration,
            int startingDazzled)
        {
            var statuses = state.Player.Statuses;
            statuses = SetStatus(
                statuses,
                StatusKind.Weak,
                Math.Max(0, Status(statuses, StatusKind.Weak) - startingWeak));
            statuses = SetStatus(
                statuses,
                StatusKind.Laceration,
                Math.Max(
                    0,
                    Status(statuses, StatusKind.Laceration) - startingLaceration));
            statuses = SetStatus(
                statuses,
                StatusKind.Dazzled,
                Math.Max(0, Status(statuses, StatusKind.Dazzled) - startingDazzled));

            var next = state with { Player = state.Player with { Statuses = statuses } };
            for (var index = 0; index < next.Enemies.Count; index++)
            {
                var enemy = next.Enemies[index];
                var enemyStatuses = SetStatus(enemy.Statuses, StatusKind.Weak, 0);
                enemyStatuses = SetStatus(
                    enemyStatuses, StatusKind.Laceration, 0);
                next = ReplaceEnemy(
                    next, index, enemy with { Statuses = enemyStatuses });
            }
            return next;
        }

        private static BattleState CheckBattleEnd(BattleState state)
        {
            var next = ApplyEnemyKillRelics(state);
            var anyLiving = next.Enemies.Any(enemy => enemy.CurrentHp > 0);
            if (anyLiving)
                return next.Player.CurrentHp <= 0
                    ? next with { Phase = BattlePhase.Result, Outcome = BattleOutcome.Defeat }
                    : ReconcileTarget(next);
            if (next.Player.CurrentHp <= 0)
                return next with { Phase = BattlePhase.Result, Outcome = BattleOutcome.Defeat };
            next = ApplyBattleWinRelics(next);
            // ボス戦はカード報酬を経由せず決着させ、最終クリアか次層への遷移かはRunEngineに委ねる。
            // 通常戦は従来どおりRewardを経由する。
            var bossBattle = next.Enemies.Count > 0 &&
                next.Enemies.All(enemy => enemy.Tier == EnemyTier.Boss);
            return bossBattle
                ? next with { Phase = BattlePhase.Result, Outcome = BattleOutcome.Victory }
                : next with { Phase = BattlePhase.Reward, Outcome = BattleOutcome.Victory };
        }

        private static BattleState ApplyEnemyKillRelics(BattleState state)
        {
            var processed = new HashSet<string>(
                state.RelicProcessedDefeatedEnemyIds);
            var next = state;
            foreach (var enemy in state.Enemies)
            {
                if (enemy.CurrentHp > 0 || !processed.Add(enemy.InstanceId))
                    continue;
                foreach (var relic in state.Relics)
                {
                    next = relic.Effect switch
                    {
                        BlockOnEnemyKillRelicEffect block =>
                            AddPlayerBlock(next, block.Amount),
                        HealOnEnemyKillRelicEffect heal =>
                            HealPlayerAmount(next, heal.Amount),
                        HealOnPoisonKillRelicEffect heal when
                            state.PoisonKilledEnemyIds.Contains(enemy.InstanceId) =>
                            HealPlayerAmount(next, heal.Amount),
                        MuscleOnPoisonKillRelicEffect muscle when
                            state.PoisonKilledEnemyIds.Contains(enemy.InstanceId) =>
                            ApplyPlayerStatus(
                                next, new MuscleStatusEffect(), muscle.Amount),
                        TemporaryMuscleOnEnemyKillRelicEffect muscle =>
                            ApplyPlayerStatus(
                                next, new MuscleStatusEffect(), muscle.Amount),
                        _ => next,
                    };
                }
            }
            return next with { RelicProcessedDefeatedEnemyIds = processed };
        }

        private static BattleState ApplyBattleWinRelics(BattleState state)
        {
            var next = state;
            foreach (var relic in state.Relics)
            {
                if (relic.Effect is HealOnBattleWinRelicEffect heal)
                    next = HealPlayerAmount(next, heal.Amount);
            }
            var eliteBattle = state.Enemies.Count > 0 &&
                state.Enemies.All(enemy => enemy.Tier == EnemyTier.Elite);
            if (eliteBattle)
            {
                foreach (var relic in state.Relics)
                {
                    if (relic.Effect is MaxHpOnEliteWinRelicEffect maxHp)
                        next = next with
                        {
                            Player = next.Player with
                            {
                                MaxHp = next.Player.MaxHp + maxHp.Amount,
                            },
                        };
                }
            }
            return next;
        }

        private static BattleState HealPlayerAmount(
            BattleState state,
            int amount) => state with
        {
            Player = state.Player with
            {
                CurrentHp = Math.Min(
                    state.Player.MaxHp,
                    state.Player.CurrentHp + Math.Max(0, amount)),
            },
        };

        private static IReadOnlyList<string> AddLog(
            IReadOnlyList<string> log,
            string message) =>
            log.Concat(new[] { message })
                .TakeLast(MaxLogEntries)
                .ToArray();

        private static BattleState AppendDefeatLogs(
            BattleState previous,
            BattleState current)
        {
            var previouslyLivingIds = previous.Enemies
                .Where(enemy => enemy.CurrentHp > 0)
                .Select(enemy => enemy.InstanceId)
                .ToHashSet();
            var next = current;
            foreach (var enemy in current.Enemies)
            {
                if (enemy.CurrentHp > 0 ||
                    !previouslyLivingIds.Contains(enemy.InstanceId))
                    continue;
                next = next with
                {
                    Log = AddLog(next.Log, $"{enemy.Name}を倒した。"),
                };
            }
            return next;
        }

        // プレイヤーが選択中の敵が撃破された場合、選択状態を残したままにしないよう
        // 生存している別の敵へ自動的にターゲットを付け替える（UI側で選択操作をしなくても
        // 次の攻撃が有効な敵に当たるようにするための保険）。
        private static BattleState ReconcileTarget(BattleState state)
        {
            if (state.Enemies.Any(enemy =>
                    enemy.InstanceId == state.SelectedEnemyInstanceId && enemy.CurrentHp > 0))
                return state;
            return state with
            {
                SelectedEnemyInstanceId = state.Enemies
                    .FirstOrDefault(enemy => enemy.CurrentHp > 0)?.InstanceId,
            };
        }

        private static int DefaultTargetIndex(BattleState state)
        {
            for (var index = 0; index < state.Enemies.Count; index++)
                if (state.Enemies[index].InstanceId == state.SelectedEnemyInstanceId &&
                    state.Enemies[index].CurrentHp > 0)
                    return index;
            for (var index = 0; index < state.Enemies.Count; index++)
                if (state.Enemies[index].CurrentHp > 0)
                    return index;
            return -1;
        }

        private static BattleState ReplaceEnemy(
            BattleState state,
            int index,
            EnemyCombatState enemy)
        {
            var enemies = state.Enemies.ToArray();
            enemies[index] = enemy;
            return state with { Enemies = enemies };
        }

        private static int Status(IReadOnlyDictionary<StatusKind, int> statuses, StatusKind kind) =>
            statuses.TryGetValue(kind, out var value) ? value : 0;

        private static StatusKind StatusKindOf(StatusEffect status) =>
            status switch
            {
                PoisonStatusEffect => StatusKind.Poison,
                LacerationStatusEffect => StatusKind.Laceration,
                WeakStatusEffect => StatusKind.Weak,
                MuscleStatusEffect => StatusKind.Muscle,
                HardeningStatusEffect => StatusKind.Hardening,
                DazzledStatusEffect => StatusKind.Dazzled,
                ArmorStatusEffect => StatusKind.Armor,
                MusclePerTurnStatusEffect => StatusKind.MusclePerTurn,
                BlockRetentionPercentStatusEffect => StatusKind.BlockRetentionPercent,
                PoisonApplicationBonusStatusEffect => StatusKind.PoisonApplicationBonus,
                FirstAttackMultiplierPercentStatusEffect => StatusKind.FirstAttackMultiplierPercent,
                DamageReflectStatusEffect => StatusKind.DamageReflect,
                DamageNegateReflectStatusEffect => StatusKind.DamageNegateReflect,
                _ => throw new UnreachableEffectException(status.GetType()),
            };

        private static string StatusDisplayName(StatusKind kind) =>
            kind switch
            {
                StatusKind.Poison => "毒",
                StatusKind.Laceration => "裂傷",
                StatusKind.Weak => "弱体",
                StatusKind.Muscle => "筋肉",
                StatusKind.Hardening => "硬化",
                StatusKind.Dazzled => "幻惑",
                StatusKind.Armor => "装甲",
                StatusKind.MusclePerTurn => "闘争心",
                StatusKind.BlockRetentionPercent => "ブロック保持率",
                StatusKind.PoisonApplicationBonus => "毒付与量",
                StatusKind.FirstAttackMultiplierPercent => "初回攻撃倍率",
                StatusKind.DamageReflect => "ダメージ反射",
                StatusKind.DamageNegateReflect => "ダメージ無効反射",
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };

        private static StatusEffect StatusEffectFor(StatusKind kind) =>
            kind switch
            {
                StatusKind.Poison => new PoisonStatusEffect(),
                StatusKind.Laceration => new LacerationStatusEffect(),
                StatusKind.Weak => new WeakStatusEffect(),
                StatusKind.Muscle => new MuscleStatusEffect(),
                StatusKind.Hardening => new HardeningStatusEffect(),
                StatusKind.Dazzled => new DazzledStatusEffect(),
                StatusKind.Armor => new ArmorStatusEffect(),
                StatusKind.MusclePerTurn => new MusclePerTurnStatusEffect(),
                StatusKind.BlockRetentionPercent => new BlockRetentionPercentStatusEffect(),
                StatusKind.PoisonApplicationBonus => new PoisonApplicationBonusStatusEffect(),
                StatusKind.FirstAttackMultiplierPercent => new FirstAttackMultiplierPercentStatusEffect(),
                StatusKind.DamageReflect => new DamageReflectStatusEffect(),
                StatusKind.DamageNegateReflect => new DamageNegateReflectStatusEffect(),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };

        private static IReadOnlyDictionary<StatusKind, int> SetStatus(
            IReadOnlyDictionary<StatusKind, int> statuses,
            StatusKind kind,
            int value)
        {
            var result = new Dictionary<StatusKind, int>(statuses);
            if (value > 0)
                result[kind] = value;
            else
                result.Remove(kind);
            return result;
        }

        private static IReadOnlyDictionary<StatusKind, int> RemoveTemporaryStrength(
            IReadOnlyDictionary<StatusKind, int> statuses,
            int temporaryStrength) =>
            temporaryStrength <= 0
                ? statuses
                : SetStatus(
                    statuses,
                    StatusKind.Muscle,
                    Math.Max(0, Status(statuses, StatusKind.Muscle) - temporaryStrength));
    }

    public sealed class UnreachableEffectException : InvalidOperationException
    {
        public UnreachableEffectException(Type effectType)
            : base($"No battle handler is registered for {effectType.Name}.")
        {
        }
    }
}
