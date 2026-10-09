using DeepseaOil.Logic.Events;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace DeepseaOil.Logic.Service {

    public sealed class PauseService : IService
    {
        private readonly IGameTime gameTime;

        private bool isPaused;
        private float pausedTime;

        public bool IsPaused => isPaused;

        public float PausedTime => pausedTime;

        public PauseService(IGameTime gameTime)
        {
            this.gameTime = gameTime;
        }

        public void Init()
        {
            isPaused = false;
            pausedTime = 0f;

            gameTime.SetTimeScale(1f);

            EventBus<RequestPause>.Subscribe(OnRequestPause);
            EventBus<RequestResume>.Subscribe(OnRequestResume);
        }

        /// <remarks>用 unscaled 累加：暂停时 dt 已缩放到 0</remarks>
        public void Tick(float deltaTime, float unscaledDeltaTime)
        {
            if (isPaused)
            {
                pausedTime += unscaledDeltaTime;
            }
        }

        public void SetPaused(bool paused)
        {
            if (isPaused == paused)
                return;

            isPaused = paused;

            if (isPaused)
            {
                gameTime.SetTimeScale(0f);

                EventBus<GamePaused>.Publish(new GamePaused());
            }
            else
            {
                gameTime.SetTimeScale(1f);

                EventBus<GameResumed>.Publish(new GameResumed());
            }
        }

        private void OnRequestPause(RequestPause request)
        {
            SetPaused(true);
        }

        private void OnRequestResume(RequestResume request)
        {
            SetPaused(false);
        }

        public void Dispose()
        {
            gameTime.SetTimeScale(1f);
            EventBus<RequestPause>.Unsubscribe(OnRequestPause);
            EventBus<RequestResume>.Unsubscribe(OnRequestResume);
        }
    }
}
