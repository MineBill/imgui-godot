using Godot;
using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ImGuiGodot.Internal;

internal class CanvasRenderer : IRenderer
{
    private class ViewportData
    {
        public RID Canvas { set; get; }
        public RID RootCanvasItem { set; get; }
    }

    private readonly Dictionary<RID, List<RID>> _canvasItemPools = new();
    private readonly Dictionary<RID, ViewportData> _vpData = new();

    public string Name => "imgui_impl_godot4_canvas";

    public void Init(ImGuiIOPtr io)
    {
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasViewports;
    }

    public void InitViewport(Viewport vp)
    {
        RID vprid = vp.GetViewportRid();

        RID canvas = RenderingServer.CanvasCreate();
        RID canvasItem = RenderingServer.CanvasItemCreate();
        RenderingServer.ViewportAttachCanvas(vprid, canvas);
        RenderingServer.CanvasItemSetParent(canvasItem, canvas);

        _vpData[vprid] = new ViewportData()
        {
            Canvas = canvas,
            RootCanvasItem = canvasItem,
        };
    }

    public void RenderDrawData(Viewport vp, ImDrawDataPtr drawData)
    {
        ViewportData vd = _vpData[vp.GetViewportRid()];
        RID parent = vd.RootCanvasItem;

        var window = (GodotImGuiWindow)GCHandle.FromIntPtr(drawData.OwnerViewport.PlatformHandle).Target;

        if (!_canvasItemPools.ContainsKey(parent))
            _canvasItemPools[parent] = new();

        var children = _canvasItemPools[parent];

        // allocate our CanvasItem pool as needed
        int neededNodes = 0;
        for (int i = 0; i < drawData.CmdListsCount; ++i)
        {
            var cmdBuf = drawData.CmdListsRange[i].CmdBuffer;
            neededNodes += cmdBuf.Size;
            for (int j = 0; j < cmdBuf.Size; ++j)
            {
                if (cmdBuf[j].ElemCount == 0)
                    --neededNodes;
            }
        }

        while (children.Count < neededNodes)
        {
            RID newChild = RenderingServer.CanvasItemCreate();
            RenderingServer.CanvasItemSetParent(newChild, parent);
            RenderingServer.CanvasItemSetDrawIndex(newChild, children.Count);
            children.Add(newChild);
        }

        // trim unused nodes
        while (children.Count > neededNodes)
        {
            int idx = children.Count - 1;
            RenderingServer.FreeRid(children[idx]);
            children.RemoveAt(idx);
        }

        // render
        drawData.ScaleClipRects(ImGui.GetIO().DisplayFramebufferScale);
        int nodeN = 0;

        for (int n = 0; n < drawData.CmdListsCount; ++n)
        {
            ImDrawListPtr cmdList = drawData.CmdListsRange[n];

            int nVert = cmdList.VtxBuffer.Size;

            var vertices = new Vector2[nVert];
            var colors = new Color[nVert];
            var uvs = new Vector2[nVert];

            for (int i = 0; i < cmdList.VtxBuffer.Size; ++i)
            {
                var v = cmdList.VtxBuffer[i];
                vertices[i] = new(v.pos.X, v.pos.Y);
                // need to reverse the color bytes
                uint rgba = v.col;
                float r = (rgba & 0xFFu) / 255f;
                rgba >>= 8;
                float g = (rgba & 0xFFu) / 255f;
                rgba >>= 8;
                float b = (rgba & 0xFFu) / 255f;
                rgba >>= 8;
                float a = (rgba & 0xFFu) / 255f;
                colors[i] = new(r, g, b, a);
                uvs[i] = new(v.uv.X, v.uv.Y);
            }

            for (int cmdi = 0; cmdi < cmdList.CmdBuffer.Size; ++cmdi)
            {
                ImDrawCmdPtr drawCmd = cmdList.CmdBuffer[cmdi];

                if (drawCmd.ElemCount == 0)
                {
                    continue;
                }

                var indices = new int[drawCmd.ElemCount];
                int idxOffset = (int)drawCmd.IdxOffset;
                for (int i = idxOffset, j = 0; i < idxOffset + drawCmd.ElemCount; ++i, ++j)
                {
                    indices[j] = cmdList.IdxBuffer[i];
                }

                Vector2[] cmdvertices = vertices;
                Color[] cmdcolors = colors;
                Vector2[] cmduvs = uvs;
                if (drawCmd.VtxOffset > 0)
                {
                    // this implementation of RendererHasVtxOffset is awful,
                    // but we can't do much better without using RenderingDevice directly
                    var localSize = cmdList.VtxBuffer.Size - drawCmd.VtxOffset;
                    cmdvertices = new Vector2[localSize];
                    cmdcolors = new Color[localSize];
                    cmduvs = new Vector2[localSize];
                    Array.Copy(vertices, drawCmd.VtxOffset, cmdvertices, 0, localSize);
                    Array.Copy(colors, drawCmd.VtxOffset, cmdcolors, 0, localSize);
                    Array.Copy(uvs, drawCmd.VtxOffset, cmduvs, 0, localSize);
                }

                RID child = children[nodeN++];

                RID texrid = Util.ConstructRID((ulong)drawCmd.GetTexID());
                RenderingServer.CanvasItemClear(child);
                Transform2D xform = Transform2D.Identity;
                if (drawData.DisplayPos != System.Numerics.Vector2.Zero)
                {
                    xform = xform.Translated(drawData.DisplayPos.ToVector2i()).Inverse();
                }
                RenderingServer.CanvasItemSetTransform(child, xform);
                RenderingServer.CanvasItemSetClip(child, true);
                RenderingServer.CanvasItemSetCustomRect(child, true, new Rect2(
                    drawCmd.ClipRect.X,
                    drawCmd.ClipRect.Y,
                    drawCmd.ClipRect.Z - drawCmd.ClipRect.X,
                    drawCmd.ClipRect.W - drawCmd.ClipRect.Y)
                );

                RenderingServer.CanvasItemAddTriangleArray(child, indices, cmdvertices, cmdcolors, cmduvs, null, null, texrid, -1);
            }
        }
    }

    public void CloseViewport(Viewport vp)
    {
        ViewportData vd = _vpData[vp.GetViewportRid()];
        ClearCanvasItems(vd.RootCanvasItem);
        RenderingServer.FreeRid(vd.RootCanvasItem);
        RenderingServer.FreeRid(vd.Canvas);
    }

    public void OnHide()
    {
        ClearCanvasItems();
    }

    public void Shutdown()
    {
        ClearCanvasItems();
        foreach (ViewportData vd in _vpData.Values)
        {
            RenderingServer.FreeRid(vd.RootCanvasItem);
            RenderingServer.FreeRid(vd.Canvas);
        }
    }

    private void ClearCanvasItems(RID rootci)
    {
        foreach (RID ci in _canvasItemPools[rootci])
        {
            RenderingServer.FreeRid(ci);
        }
    }

    private void ClearCanvasItems()
    {
        foreach (RID parent in _canvasItemPools.Keys)
        {
            ClearCanvasItems(parent);
        }
        _canvasItemPools.Clear();
    }
}
