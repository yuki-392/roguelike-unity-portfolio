using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Roguelike.Domain.Battle;
using Roguelike.Domain.Data;
using Roguelike.Domain.Models;
using Roguelike.Domain.Rng;

namespace Roguelike.Domain.Tests
{
    // BattleEngineの基本フロー（開始・カードプレイ・ターン終了・状態異常の増減・
    // 反射系ステータス）を対象にした単体テスト集。ログ文言を検証するテストは、
    // 表示文言の変更が意図せず起きていないかも兼ねて確認している。
    public sealed class BattleEngineCoreTests
    {
        [Test]
        public void StartBattle_ResetsCombatResourcesAndDrawsFiveCards()
        {
            var cards = new[]
            {
                Card("card-001"), Card("punch-2"), Card("punch-3"),
                Card("punch-4"), Card("punch-5"), Card("punch-6"),
            };
            var player = Player(cards, currentHp: 73) with
            {
                Block = 99,
                Energy = 0,
                Hand = new[] { cards[0] },
                DrawPile = new[] { cards[1] },
                DiscardPile = new[] { cards[2], cards[3], cards[4], cards[5] },
                Statuses = new Dictionary<StatusKind, int> { [StatusKind.Poison] = 5 },
            };

            var state = BattleEngine.StartBattle(
                player,
                new[] { Enemy("enemy-1", hp: 20, new IdleEnemyAction()) },
                new SequenceRng(0));

            Assert.That(state.Player.CurrentHp, Is.EqualTo(73));
            Assert.That(state.Player.Block, Is.EqualTo(0));
            Assert.That(state.Player.Energy, Is.EqualTo(state.Player.MaxEnergy));
            Assert.That(state.Player.Statuses.Count, Is.EqualTo(0));
            Assert.That(state.Player.Hand.Count, Is.EqualTo(5));
            Assert.That(state.Player.DrawPile.Count, Is.EqualTo(1));
            Assert.That(state.Player.DiscardPile.Count, Is.EqualTo(0));
            Assert.That(state.SelectedEnemyInstanceId, Is.EqualTo("enemy-1"));
        }

        [Test]
        public void DrawCards_StopsAtNineAndMovesOverflowToDiscard()
        {
            var hand = Enumerable.Range(0, 8)
                .Select(index => Card($"hand-{index}"))
                .ToArray();
            var accepted = Card("accepted");
            var overflow = Card("overflow");
            var state = BattleState.Create(
                Player(new[] { accepted, overflow }) with
                {
                    Hand = hand,
                    DrawPile = new[] { accepted, overflow },
                },
                new[] { Enemy("enemy-1", 20, new IdleEnemyAction()) });

            var result = BattleEngine.ApplyEffect(
                state,
                new DrawCardEffect(2),
                new SequenceRng(0));

            Assert.That(result.Player.Hand.Count, Is.EqualTo(9));
            Assert.That(result.Player.Hand.Last().Id, Is.EqualTo(accepted.Id));
            Assert.That(result.Player.DiscardPile.Select(card => card.Id),
                Is.EqualTo(new[] { overflow.Id }));
            Assert.That(result.CardsDrawnThisTurn, Is.EqualTo(2));
            Assert.That(result.Log.Last(), Is.EqualTo(
                "手札上限のため、1枚を捨て札に送った。"));
        }

        [Test]
        public void DrawCards_DoesNotReshuffleOverflowDuringSameDraw()
        {
            var hand = Enumerable.Range(0, 9)
                .Select(index => Card($"hand-{index}"))
                .ToArray();
            var overflow = Card("overflow");
            var state = BattleState.Create(
                Player(new[] { overflow }) with
                {
                    Hand = hand,
                    DrawPile = new[] { overflow },
                },
                new[] { Enemy("enemy-1", 20, new IdleEnemyAction()) });

            var result = BattleEngine.ApplyEffect(
                state,
                new DrawCardEffect(2),
                new SequenceRng(0));

            Assert.That(result.Player.Hand.Count, Is.EqualTo(9));
            Assert.That(result.Player.DiscardPile.Select(card => card.Id),
                Is.EqualTo(new[] { overflow.Id }));
            Assert.That(result.CardsDrawnThisTurn, Is.EqualTo(1));
        }

        [Test]
        public void PlayCard_AttackConsumesEnergyDamagesThroughBlockAndMovesCard()
        {
            var card = Card("punch-play");
            var player = Player(new[] { card }) with { Hand = new[] { card }, Energy = 3 };
            var state = BattleState.Create(
                player,
                new[] { Enemy("enemy-1", hp: 20, new IdleEnemyAction()) with { Block = 3 } });

            var result = BattleEngine.PlayCard(state, card.Id, new SequenceRng(0));

            Assert.That(result.Player.Energy, Is.EqualTo(2));
            Assert.That(result.Player.Hand.Count, Is.EqualTo(0));
            Assert.That(result.Player.DiscardPile.Count, Is.EqualTo(1));
            Assert.That(result.Enemies[0].Block, Is.EqualTo(0));
            Assert.That(result.Enemies[0].CurrentHp, Is.EqualTo(15));
            Assert.That(result.CardsPlayedThisTurn, Is.EqualTo(1));
            Assert.That(result.AttackCardsPlayedThisTurn, Is.EqualTo(1));
        }

        [TestCase(7, 3, 100)]
        [TestCase(11, 0, 99)]
        public void PlayCard_AttackConsumesEnemyBlockBeforeHealth(
            int damage,
            int expectedBlock,
            int expectedHp)
        {
            var card = Card("block-absorption") with
            {
                Category = CardCategory.Attack,
                Effects = new CardEffect[] { new AttackCardEffect(damage) },
                Cost = new FixedCardCost(1),
            };
            var state = BattleState.Create(
                Player(new[] { card }) with
                {
                    Hand = new[] { card },
                },
                new[] { Enemy("enemy-1", hp: 100, new IdleEnemyAction()) with
                {
                    Block = 10,
                } });

            var result = BattleEngine.PlayCard(
                state, card.Id, new SequenceRng(0));

            Assert.That(result.Enemies[0].Block, Is.EqualTo(expectedBlock));
            Assert.That(result.Enemies[0].CurrentHp, Is.EqualTo(expectedHp));
        }

        [Test]
        public void PlayCard_DefeatingOneOfMultipleEnemiesLogsDefeatAndContinuesBattle()
        {
            var card = Card("finishing-attack");
            var player = Player(new[] { card }) with
            {
                Hand = new[] { card },
                Energy = 3,
            };
            var defeated = Enemy(
                "enemy-defeated", hp: 8, new IdleEnemyAction()) with
            {
                Name = "スライム",
            };
            var survivor = Enemy(
                "enemy-survivor", hp: 20, new IdleEnemyAction()) with
            {
                Name = "ゴースト",
            };
            var state = BattleState.Create(
                player, new[] { defeated, survivor });

            var result = BattleEngine.PlayCard(
                state, card.Id, new SequenceRng(0));

            Assert.That(result.Enemies[0].CurrentHp, Is.EqualTo(0));
            Assert.That(result.Enemies[1], Is.EqualTo(survivor));
            Assert.That(result.Phase, Is.EqualTo(BattlePhase.Battle));
            Assert.That(result.Outcome, Is.Null);
            Assert.That(result.SelectedEnemyInstanceId,
                Is.EqualTo(survivor.InstanceId));
            Assert.That(result.Log, Does.Contain("スライムを倒した。"));
        }

        [Test]
        public void PlayCard_WithInsufficientEnergyAddsReasonToLog()
        {
            var card = Card("expensive") with { Cost = new FixedCardCost(4) };
            var state = BattleState.Create(
                Player(new[] { card }) with { Hand = new[] { card }, Energy = 3 },
                new[] { Enemy("enemy-1", hp: 20, new IdleEnemyAction()) });

            var result = BattleEngine.PlayCard(state, card.Id, new SequenceRng(0));

            Assert.That(result.Player, Is.EqualTo(state.Player));
            Assert.That(result.Enemies, Is.EqualTo(state.Enemies));
            Assert.That(result.Log, Is.EqualTo(new[] { "エナジーが足りません" }));
        }

        [Test]
        public void PlayCard_BlockUsesArmorBonus()
        {
            var card = Card("block") with
            {
                Category = CardCategory.Defense,
                Effects = new CardEffect[] { new BlockCardEffect(6) },
            };
            var player = Player(new[] { card }) with
            {
                Hand = new[] { card },
                Statuses = new Dictionary<StatusKind, int> { [StatusKind.Armor] = 2 },
            };

            var result = BattleEngine.PlayCard(
                BattleState.Create(player, new[] { Enemy("enemy-1", 20, new IdleEnemyAction()) }),
                card.Id,
                new SequenceRng(0));

            Assert.That(result.Player.Block, Is.EqualTo(8));
        }

        [Test]
        public void PlayCard_TacticalPlanningDrawsTwoAndWaitsForOneDiscard()
        {
            var tacticalPlanning = GameCatalog.StartingDecks
                .SelectMany(deck => deck.Cards)
                .First(card => card.Id == "senjutsuseiri-9");
            var kept = Card("kept");
            var drawnFirst = Card("drawn-first");
            var drawnSecond = Card("drawn-second");
            var state = BattleState.Create(
                Player(new[] { drawnFirst, drawnSecond }) with
                {
                    Hand = new[] { tacticalPlanning, kept },
                    DrawPile = new[] { drawnFirst, drawnSecond },
                },
                new[] { Enemy("enemy-1", 20, new IdleEnemyAction()) });

            var result = BattleEngine.PlayCard(
                state, tacticalPlanning.Id, new SequenceRng(0));

            Assert.That(result.Player.Hand.Select(card => card.Id), Is.EquivalentTo(
                new[] { kept.Id, drawnFirst.Id, drawnSecond.Id }));
            Assert.That(result.PendingDiscardCount, Is.EqualTo(1));
            Assert.That(result.Player.DiscardPile.Select(card => card.Id),
                Does.Contain(tacticalPlanning.Id));
        }

        [Test]
        public void SelectDiscardCard_MovesSelectedCardAndClearsPendingChoice()
        {
            var first = Card("first");
            var second = Card("second");
            var state = BattleState.Create(
                Player(Array.Empty<CardDefinition>()) with
                {
                    Hand = new[] { first, second },
                },
                new[] { Enemy("enemy-1", 20, new IdleEnemyAction()) }) with
            {
                PendingDiscardCount = 1,
            };

            var result = BattleEngine.SelectDiscardCard(state, second.Id);

            Assert.That(result.Player.Hand.Select(card => card.Id),
                Is.EqualTo(new[] { first.Id }));
            Assert.That(result.Player.DiscardPile.Select(card => card.Id),
                Is.EqualTo(new[] { second.Id }));
            Assert.That(result.PendingDiscardCount, Is.EqualTo(0));
        }

        [Test]
        public void PendingDiscard_BlocksPlayingCardsAndEndingTurn()
        {
            var card = Card("blocked");
            var state = BattleState.Create(
                Player(Array.Empty<CardDefinition>()) with { Hand = new[] { card } },
                new[] { Enemy("enemy-1", 20, new IdleEnemyAction()) }) with
            {
                PendingDiscardCount = 1,
            };

            Assert.That(
                BattleEngine.PlayCard(state, card.Id, new SequenceRng(0)),
                Is.EqualTo(state));
            Assert.That(
                BattleEngine.EndPlayerTurn(state, new SequenceRng(0)),
                Is.EqualTo(state));
        }

        [Test]
        public void EndPlayerTurn_EnemyAttackUsesMuscleWeakAndPlayerLaceration()
        {
            var player = Player(Array.Empty<CardDefinition>()) with
            {
                CurrentHp = 100,
                Block = 5,
                Statuses = new Dictionary<StatusKind, int> { [StatusKind.Laceration] = 1 },
            };
            var enemy = Enemy("enemy-1", 20, new AttackEnemyAction(10)) with
            {
                Statuses = new Dictionary<StatusKind, int>
                {
                    [StatusKind.Muscle] = 3,
                    [StatusKind.Weak] = 1,
                },
            };

            var result = BattleEngine.EndPlayerTurn(
                BattleState.Create(player, new[] { enemy }),
                new SequenceRng(0));

            // 10 + 筋肉3 - 弱体1 + 裂傷5 = 17ダメージ。うち5はブロックが吸収する。
            Assert.That(result.Player.CurrentHp, Is.EqualTo(88));
            Assert.That(result.Player.Block, Is.EqualTo(0));
            Assert.That(result.Turn, Is.EqualTo(TurnOwner.Player));
        }

        [Test]
        public void PlayCard_KillingLastNonBossEnemyMovesToReward()
        {
            var card = Card("lethal");
            var state = BattleState.Create(
                Player(new[] { card }) with { Hand = new[] { card } },
                new[] { Enemy("enemy-1", hp: 8, new IdleEnemyAction()) });

            var result = BattleEngine.PlayCard(state, card.Id, new SequenceRng(0));

            Assert.That(result.Phase, Is.EqualTo(BattlePhase.Reward));
            Assert.That(result.Outcome, Is.EqualTo(BattleOutcome.Victory));
        }

        [Test]
        public void PlayCard_UsesOriginalGameLogWordingAndKeepsLatestTwenty()
        {
            var card = Card("logged-card");
            var state = BattleState.Create(
                Player(new[] { card }) with { Hand = new[] { card } },
                new[] { Enemy("enemy-1", hp: 30, new IdleEnemyAction()) }) with
            {
                Log = Enumerable.Range(0, 20)
                    .Select(index => $"ログ{index}").ToArray(),
            };

            var result = BattleEngine.PlayCard(state, card.Id, new SequenceRng(0));

            Assert.That(result.Log.Count, Is.EqualTo(20));
            Assert.That(result.Log, Does.Contain(
                $"プレイヤーが{card.Name}をプレイした。"));
            Assert.That(result.Log.Last(),
                Is.EqualTo("プレイヤーが8ダメージを与えた。テスト敵HP: 22"));
            Assert.That(result.Log, Does.Not.Contain("ログ0"));
        }

        [Test]
        public void EndTurn_LogsTurnEndAndEnemyAttackLikeOriginalGame()
        {
            var state = BattleState.Create(
                Player(Array.Empty<CardDefinition>()),
                new[] { Enemy("enemy-1", 20, new AttackEnemyAction(7)) });

            var result = BattleEngine.EndPlayerTurn(state, new SequenceRng(0));

            Assert.That(result.Log[0],
                Is.EqualTo("プレイヤーがターンを終了した。"));
            Assert.That(result.Log[1],
                Is.EqualTo("テスト敵が7ダメージを与えた。プレイヤーHP: 93"));
        }

        [Test]
        public void BlockCard_LogsGainedAndCurrentBlockLikeOriginalGame()
        {
            var source = GameCatalog.Cards.First(card =>
                card.Effects.Any(effect => effect is BlockCardEffect));
            var card = source with { Id = "logged-block" };
            var state = BattleState.Create(
                Player(new[] { card }) with
                {
                    Hand = new[] { card },
                    Block = 3,
                },
                new[] { Enemy("enemy-1", 30, new IdleEnemyAction()) });

            var result = BattleEngine.PlayCard(state, card.Id, new SequenceRng(0));
            var gained = result.Player.Block - 3;

            Assert.That(result.Log.Last(),
                Is.EqualTo(
                    $"プレイヤーが{gained}ブロックを得た。ブロック: " +
                    $"{result.Player.Block}"));
        }

        [Test]
        public void EndTurn_AppliesHardeningRetentionPerTurnAndStatusDecay()
        {
            var player = Player(Array.Empty<CardDefinition>()) with
            {
                Block = 20,
                Statuses = new Dictionary<StatusKind, int>
                {
                    [StatusKind.Weak] = 2,
                    [StatusKind.Laceration] = 1,
                    [StatusKind.Dazzled] = 1,
                    [StatusKind.Hardening] = 3,
                    [StatusKind.MusclePerTurn] = 2,
                    [StatusKind.BlockRetentionPercent] = 50,
                },
            };
            var enemy = Enemy("enemy-1", 20, new ApplyStatusEnemyAction(
                EnemyStatusTarget.Player, new WeakStatusEffect(), 1, 1)) with
            {
                Block = 99,
                Statuses = new Dictionary<StatusKind, int>
                {
                    [StatusKind.Hardening] = 4,
                    [StatusKind.Weak] = 3,
                    [StatusKind.Laceration] = 2,
                },
            };

            var result = BattleEngine.EndPlayerTurn(
                BattleState.Create(player, new[] { enemy }),
                new SequenceRng(0));

            Assert.That(result.Player.Block, Is.EqualTo(13));
            Assert.That(result.Player.Statuses[StatusKind.Muscle], Is.EqualTo(2));
            Assert.That(result.Player.Statuses[StatusKind.Weak], Is.EqualTo(1));
            Assert.That(result.Player.Statuses.ContainsKey(StatusKind.Laceration), Is.False);
            Assert.That(result.Player.Statuses.ContainsKey(StatusKind.Dazzled), Is.False);
            Assert.That(result.Enemies[0].Block, Is.EqualTo(4));
            Assert.That(result.Enemies[0].Statuses.ContainsKey(StatusKind.Weak), Is.False);
            Assert.That(result.Enemies[0].Statuses.ContainsKey(StatusKind.Laceration), Is.False);
        }

        [Test]
        public void EnemyAttack_DamageNegateReflectPreventsDamageAndHitsAttacker()
        {
            var state = BattleState.Create(
                Player(Array.Empty<CardDefinition>()) with
                {
                    Statuses = new Dictionary<StatusKind, int>
                    {
                        [StatusKind.DamageNegateReflect] = 1,
                    },
                },
                new[] { Enemy("enemy-1", 20, new IdleEnemyAction()) });

            var result = BattleEngine.ExecuteEnemyAction(
                state, "enemy-1", new AttackEnemyAction(7), new SequenceRng(0));

            Assert.That(result.Player.CurrentHp, Is.EqualTo(100));
            Assert.That(
                result.Player.Statuses.ContainsKey(StatusKind.DamageNegateReflect),
                Is.False);
            Assert.That(result.Enemies[0].CurrentHp, Is.EqualTo(13));
        }

        private static CardDefinition Card(string id) =>
            GameCatalog.Cards.Single(card => card.Id == "card-001") with { Id = id };

        private static PlayerCombatState Player(
            IReadOnlyList<CardDefinition> deck,
            int currentHp = 100) =>
            new(
                MaxHp: 100,
                CurrentHp: currentHp,
                Block: 0,
                Energy: 3,
                MaxEnergy: 3,
                Hand: Array.Empty<CardDefinition>(),
                DrawPile: deck,
                DiscardPile: Array.Empty<CardDefinition>(),
                ExhaustPile: Array.Empty<CardDefinition>(),
                Statuses: new Dictionary<StatusKind, int>());

        private static EnemyCombatState Enemy(
            string instanceId,
            int hp,
            EnemyAction action) =>
            new(
                InstanceId: instanceId,
                EnemyId: "test-enemy",
                Name: "テスト敵",
                MaxHp: hp,
                CurrentHp: hp,
                Block: 0,
                Tier: EnemyTier.Normal,
                BattleTurn: 0,
                Statuses: new Dictionary<StatusKind, int>(),
                NextAction: action);
    }
}
