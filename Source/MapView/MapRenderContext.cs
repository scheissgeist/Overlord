using System;
using RimWorld;
using Verse;

namespace Overlord
{
    /// <summary>
    /// Temporary render override used while capturing an off-screen viewer map frame.
    /// RimWorld map meshes are selected from CameraDriver.CurrentViewRect, not just
    /// from the Unity camera transform, so the off-screen capture needs this scope.
    /// </summary>
    public static class MapRenderContext
    {
        private static CellRect overrideViewRect = CellRect.Empty;
        private static bool forceCloseZoom;

        // The override may ONLY affect the camera while an off-screen capture Render()
        // is actively executing. Without this gate the camera patches applied on EVERY
        // game-wide read of CurrentViewRect/CurrentZoom whenever the override happened
        // to be set — and because WaitForEndOfFrame does not guarantee our capture runs
        // after all of RimWorld's live camera reads, the live view could freeze to the
        // viewer rect (zoom stuck / minimap zooms but main map doesn't). The capture is
        // synchronous on the main thread, so a plain static flag is sufficient and
        // correct: it is true only for the duration of the borrowed gameCamera.Render().
        private static bool captureActive;

        public static void MarkCaptureActive(bool active) => captureActive = active;

        // Patches consult these — they gate on captureActive so the override is inert
        // outside the actual capture Render(), never touching the live presented frame.
        public static bool HasViewRectOverride => captureActive && !overrideViewRect.IsEmpty;
        public static bool ForceCloseZoom => captureActive && forceCloseZoom;
        public static CellRect ViewRectOverride => overrideViewRect;

        public static IDisposable Begin(CellRect viewRect, bool closeZoom)
        {
            var previousRect = overrideViewRect;
            bool previousZoom = forceCloseZoom;
            overrideViewRect = viewRect;
            forceCloseZoom = closeZoom;
            return new Scope(previousRect, previousZoom);
        }

        private sealed class Scope : IDisposable
        {
            private readonly CellRect previousRect;
            private readonly bool previousZoom;
            private bool disposed;

            public Scope(CellRect previousRect, bool previousZoom)
            {
                this.previousRect = previousRect;
                this.previousZoom = previousZoom;
            }

            public void Dispose()
            {
                if (disposed)
                    return;

                overrideViewRect = previousRect;
                forceCloseZoom = previousZoom;
                disposed = true;
            }
        }
    }
}
