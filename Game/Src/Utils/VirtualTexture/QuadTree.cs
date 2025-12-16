using Godot;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace ProjectM;

internal unsafe struct QuadTree
{
    public int level;

    public Rect2I region;

    public int mappingSlot;

    public QuadTree* leftUp;
    public QuadTree* leftDown;
    public QuadTree* rightUp;
    public QuadTree* rightDown;

    public QuadTree(Rect2I region, int level)
    {
        this.region = region;
        this.level = level;
        this.mappingSlot = 0;
        leftUp = null;
        leftDown = null;
        rightUp = null;
        rightDown = null;
    }

    public void Add(VirtualPageID page, int mappingSlot)
    {
        int scale = 1 << page.mip;
        int x = page.x * scale;
        int y = page.y * scale;

        QuadTree node = this;
        while (page.mip < node.level)
        {
            QuadTree** subNodes = stackalloc QuadTree*[] { node.leftUp, node.leftDown, node.rightUp, node.rightDown };
            for (int i = 0; i < 4; ++i)
            {
                Rect2I region = GetRectangle(ref node, i);
                if (region.HasPoint(new Vector2I(x, y)))
                {
                    // Create a new one if needed
                    if (subNodes[i] == null)
                    {
                        subNodes[i] = (QuadTree*)NativeMemory.Alloc((nuint)sizeof(QuadTree));
                        *subNodes[i] = new QuadTree(region, node.level - 1);
                    }

                    node = *subNodes[i];
                }
            }
        }
        // We have created the correct node, now set the mapping
        node.mappingSlot = mappingSlot;
    }
    static Rect2I GetRectangle(ref QuadTree node, int index)
    {
        int x = node.region.Position.X;
        int y = node.region.Position.Y;
        int w = (int)Math.Ceiling(node.region.Size.X * 0.5f);
        int h = (int)Math.Ceiling(node.region.Size.Y * 0.5f);

        switch (index)
        {
            case 0: return new Rect2I(x, y, w, h);
            case 1: return new Rect2I(x + w, y, w, h);
            case 2: return new Rect2I(x + w, y + h, w, h);
            case 3: return new Rect2I(x, y + h, w, h);
        }

        throw new ArgumentOutOfRangeException("index");
    }
}
