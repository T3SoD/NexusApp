namespace NexusApp.Views;

// Hand-authored dock icons that are NOT part of the auto-generated picks in DockIconSpecs.cs.
// AnimatedDockIcon falls back to this set when a key is missing from the generated JSON, so
// re-running the picks gallery can never drop these entries.
// Currently: "guides" (folded map, adopted from the approved Mission Guides mock); "trade"
// (balance scale, adopted from the approved Trade dock icon mock, candidate 3 of 10 - see
// nexus-design-lab/trade-icon/index.html).
public static class DockIconSpecsCustom
{
    public const string Json = """
{
 "guides": {
  "view": [
   -6,
   -6,
   36,
   36
  ],
  "stroke": 1.5,
  "parts": [
   {
    "id": "p0",
    "el": "path",
    "d": "M4,7 L9.5,5 L14.5,7 L20,5 L20,17 L14.5,19 L9.5,17 L4,19 Z"
   },
   {
    "id": "p1",
    "el": "path",
    "d": "M9.5,5 L9.5,17"
   },
   {
    "id": "p2",
    "el": "path",
    "d": "M14.5,7 L14.5,19"
   }
  ],
  "hover": {
   "duration": 0.22,
   "ease": "easeOut",
   "tracks": [
    {
     "part": "p0",
     "scale": [
      1,
      1.06,
      1
     ],
     "origin": [
      12,
      12
     ]
    },
    {
     "part": "p1",
     "scale": [
      1,
      1.06,
      1
     ],
     "origin": [
      12,
      12
     ]
    },
    {
     "part": "p2",
     "scale": [
      1,
      1.06,
      1
     ],
     "origin": [
      12,
      12
     ]
    }
   ]
  },
  "selected": {
   "duration": 0.55,
   "ease": "settle",
   "tracks": [
    {
     "part": "p0",
     "draw": true,
     "scale": [
      0.94,
      1
     ],
     "origin": [
      12,
      12
     ],
     "delay": 0.0
    },
    {
     "part": "p1",
     "draw": true,
     "delay": 0.1
    },
    {
     "part": "p2",
     "draw": true,
     "delay": 0.16
    }
   ]
  }
 },
 "trade": {
  "view": [
   -6,
   -6,
   36,
   36
  ],
  "stroke": 1.5,
  "parts": [
   {
    "id": "p0",
    "el": "path",
    "d": "M12 4 L12 20.5"
   },
   {
    "id": "p1",
    "el": "line",
    "x1": 8,
    "y1": 20.5,
    "x2": 16,
    "y2": 20.5
   },
   {
    "id": "p2",
    "el": "line",
    "x1": 5,
    "y1": 8,
    "x2": 19,
    "y2": 8
   },
   {
    "id": "p3",
    "el": "line",
    "x1": 5,
    "y1": 8,
    "x2": 5,
    "y2": 12.5
   },
   {
    "id": "p4",
    "el": "path",
    "d": "M1.8 12.5 A 3.2 3.2 0 0 0 8.2 12.5"
   },
   {
    "id": "p5",
    "el": "line",
    "x1": 19,
    "y1": 8,
    "x2": 19,
    "y2": 12.5
   },
   {
    "id": "p6",
    "el": "path",
    "d": "M15.8 12.5 A 3.2 3.2 0 0 0 22.2 12.5"
   },
   {
    "id": "f_tl",
    "el": "path",
    "d": "M-2 -5 L-5 -5 L-5 -2",
    "sw": 1.0
   },
   {
    "id": "f_tr",
    "el": "path",
    "d": "M26 -5 L29 -5 L29 -2",
    "sw": 1.0
   },
   {
    "id": "f_bl",
    "el": "path",
    "d": "M-5 26 L-5 29 L-2 29",
    "sw": 1.0
   },
   {
    "id": "f_br",
    "el": "path",
    "d": "M29 26 L29 29 L26 29",
    "sw": 1.0
   },
   {
    "id": "f_pip",
    "el": "circle",
    "cx": 29,
    "cy": -5,
    "r": 1.3,
    "fill": "#7FE9E0"
   }
  ],
  "hover": {
   "duration": 0.22,
   "ease": "easeOut",
   "tracks": [
    {
     "part": "p0",
     "scale": [
      1,
      1.06,
      1
     ],
     "origin": [
      12,
      12
     ]
    },
    {
     "part": "p1",
     "scale": [
      1,
      1.06,
      1
     ],
     "origin": [
      12,
      12
     ]
    },
    {
     "part": "p2",
     "scale": [
      1,
      1.06,
      1
     ],
     "origin": [
      12,
      12
     ]
    },
    {
     "part": "p3",
     "scale": [
      1,
      1.06,
      1
     ],
     "origin": [
      12,
      12
     ]
    },
    {
     "part": "p4",
     "scale": [
      1,
      1.06,
      1
     ],
     "origin": [
      12,
      12
     ]
    },
    {
     "part": "p5",
     "scale": [
      1,
      1.06,
      1
     ],
     "origin": [
      12,
      12
     ]
    },
    {
     "part": "p6",
     "scale": [
      1,
      1.06,
      1
     ],
     "origin": [
      12,
      12
     ]
    },
    {
     "part": "f_tl",
     "x": [
      0,
      -1,
      0
     ],
     "y": [
      0,
      -1,
      0
     ]
    },
    {
     "part": "f_tr",
     "x": [
      0,
      1,
      0
     ],
     "y": [
      0,
      -1,
      0
     ]
    },
    {
     "part": "f_bl",
     "x": [
      0,
      -1,
      0
     ],
     "y": [
      0,
      1,
      0
     ]
    },
    {
     "part": "f_br",
     "x": [
      0,
      1,
      0
     ],
     "y": [
      0,
      1,
      0
     ]
    },
    {
     "part": "f_pip",
     "scale": [
      1,
      1.25,
      1
     ]
    }
   ]
  },
  "selected": {
   "duration": 0.55,
   "ease": "settle",
   "tracks": [
    {
     "part": "p0",
     "draw": true,
     "delay": 0.0
    },
    {
     "part": "p1",
     "draw": true,
     "delay": 0.05
    },
    {
     "part": "p2",
     "draw": true,
     "delay": 0.1
    },
    {
     "part": "p3",
     "draw": true,
     "delay": 0.15
    },
    {
     "part": "p4",
     "draw": true,
     "delay": 0.2
    },
    {
     "part": "p5",
     "draw": true,
     "delay": 0.25
    },
    {
     "part": "p6",
     "draw": true,
     "delay": 0.3
    },
    {
     "part": "f_tl",
     "x": [
      -3,
      0
     ],
     "y": [
      -3,
      0
     ],
     "opacity": [
      0,
      1
     ],
     "delay": 0.0
    },
    {
     "part": "f_tr",
     "x": [
      3,
      0
     ],
     "y": [
      -3,
      0
     ],
     "opacity": [
      0,
      1
     ],
     "delay": 0.03
    },
    {
     "part": "f_bl",
     "x": [
      -3,
      0
     ],
     "y": [
      3,
      0
     ],
     "opacity": [
      0,
      1
     ],
     "delay": 0.06
    },
    {
     "part": "f_br",
     "x": [
      3,
      0
     ],
     "y": [
      3,
      0
     ],
     "opacity": [
      0,
      1
     ],
     "delay": 0.09
    },
    {
     "part": "f_pip",
     "scale": [
      0,
      1
     ],
     "ease": "back",
     "delay": 0.35
    }
   ]
  }
 }
}
""";
}
