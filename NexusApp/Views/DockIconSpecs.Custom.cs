namespace NexusApp.Views;

// Hand-authored dock icons that are NOT part of the auto-generated picks in DockIconSpecs.cs.
// AnimatedDockIcon falls back to this set when a key is missing from the generated JSON, so
// re-running the picks gallery can never drop these entries.
// Currently: "guides" (folded map, adopted from the approved Mission Guides mock).
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
 }
}
""";
}
