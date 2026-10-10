using System.Collections.Generic;
using DeepseaOil.Data;
using DeepseaOil.Logic.Combat;
using DeepseaOil.Logic.Grid;
using DeepseaOil.Logic.Projectile;
using UnityEngine;
using cfg.dso;

namespace DeepseaOil.Presentation.Ball
{
    /// <summary>球的调度器，由组合根显式造与驱动</summary>
    /// <remarks>飞行与改格都在渲染帧，只有冲量跨物理帧。落地只做一件事：把（落点格, 球种）交给格子层，反应与冲击全在那里闭环。</remarks>
    public sealed class BallDirector
    {
        private readonly List<BallActor> _flying = new List<BallActor>();

        private readonly Dictionary<BallType, ProjectileSpec> _balls = new Dictionary<BallType, ProjectileSpec>();

        private GridLogic _grid;
        private ImpulseExecutor _impulses;
        private Transform _ballRoot;

        public int FlyingCount => _flying.Count;

        public bool IsReady => _grid != null;

        /// <remarks>ballRoot=null 建在场景根下</remarks>
        public void Attach(
            GridLogic grid,
            IReadOnlyList<ProjectileSpec> balls,
            ImpulseExecutor impulses,
            Transform ballRoot)
        {
            _grid = grid;
            _impulses = impulses;
            _ballRoot = ballRoot;

            _balls.Clear();

            if (balls == null) return;

            for (int i = 0; i < balls.Count; i++)
            {
                ProjectileSpec ball = balls[i];

                _balls[ball.Type] = ball;
            }
        }

        /// <summary>推进一个渲染帧；落地帧改格＋入队冲量再回收；deltaTime=0（暂停）冻结</summary>
        public void Tick(float deltaTime)
        {
            // 倒序：正序删除会跳过下一个元素
            for (int i = _flying.Count - 1; i >= 0; i--)
            {
                BallActor ball = _flying[i];

                if (ball == null)
                {
                    _flying.RemoveAt(i);
                    continue;
                }

                ball.Tick(deltaTime);

                if (ball.IsAlive) continue;

                ball.Dispose();
                _flying.RemoveAt(i);
            }
        }

        /// <summary>按已裁决通过的意图投一颗球；球种没定义时丢弃</summary>
        public bool Throw(in ThrowIntent intent)
        {
            if (!_balls.TryGetValue(intent.Ball, out ProjectileSpec definition))
            {
                Debug.LogError($"[Ball] projectile 表里没有球种 {intent.Ball}，这次投掷被丢弃。");
                return false;
            }

            float distance = Vector2.Distance(intent.Origin, intent.Target);

            var data = new ProjectileTrajectory(intent.Ball, intent.Origin, intent.Target, distance, definition);

            _flying.Add(new BallActor(in data, definition, _ballRoot, OnLanded));

            return true;
        }

        /// <summary>清空在飞球与待施加冲量；冲量不清会砸在下一局的箱子上</summary>
        public void ClearAll()
        {
            for (int i = 0; i < _flying.Count; i++)
            {
                _flying[i]?.Dispose();
            }

            _flying.Clear();

            _impulses?.Clear();
        }

        /// <summary>一次落地的完整结算，先改格再排冲量</summary>
        /// <remarks>顺序即语义：改格在同一帧内结算落地冲击（含首跳伤害与连锁）；冲量下一物理帧才生效。</remarks>
        private void OnLanded(BallActor ball, Vector2 point)
        {
            if (_grid != null && _grid.Geometry.IsValid)
            {
                _grid.OnBallHit(_grid.WorldToCell(point), ball.Definition.Type);
            }

            _impulses?.Enqueue(point, ball.Definition.Tuning);
        }
    }
}
