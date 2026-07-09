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

        public static bool HasViewRectOverride => !overrideViewRect.IsEmpty;
        public static bool ForceCloseZoom => forceCloseZoom;
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
