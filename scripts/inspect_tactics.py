"""Reads a learned-tactics file and prints what the Pursuer would do in typical situations.

Usage: python scripts/inspect_tactics.py [path-to-dion_hunted_tactics_*.txt]
"""
import math, sys, glob, os

TACTICS = ["Throw", "Reposition", "CloseIn", "Wait", "Slide", "Pounce", "SlidePounce", "Roll", "Backflip", "FlipThrow"]
GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Rain World\RainWorld_Data\StreamingAssets\ModConfigs"

def load(path):
    text = open(path, encoding="utf-8").read().strip()
    fields = dict(part.split("=", 1) for part in text.split("|"))
    nums = [float(x) for x in fields["net"].split(",")]
    n_in, n_hid, n_out = int(nums[0]), int(nums[1]), int(nums[2])
    w = nums[4:]
    k = 0
    w1 = w[k:k + n_in * n_hid]; k += n_in * n_hid
    b1 = w[k:k + n_hid]; k += n_hid
    w2 = w[k:k + n_hid * n_out]; k += n_hid * n_out
    b2 = w[k:k + n_out]
    return fields, (n_in, n_hid, n_out, w1, b1, w2, b2)

def forward(net, x):
    n_in, n_hid, n_out, w1, b1, w2, b2 = net
    hid = [math.tanh(b1[h] + sum(w1[i * n_hid + h] * x[i] for i in range(n_in))) for h in range(n_hid)]
    return [b2[o] + sum(w2[h * n_out + o] * hid[h] for h in range(n_hid)) for o in range(n_out)]

def situation(dx, dy, los=1, since=0, armed=1, moving=0.3, held=1/3, threat=0.2, good=None, bomb=0, ground=1, incoming=0):
    dist = math.hypot(dx, dy)
    if good is None:
        d = dy / dist if dist else 0
        good = 1.0 if (-0.2 <= d <= 0.05 and dist <= 520 and los) else 0.0
    return [max(-1, min(1, dx / 400)), max(-1, min(1, dy / 400)), min(1, dist / 400), los, min(1, since / 400),
            1.0 if dy > 20 else 0.0, armed, moving, held, threat, good, bomb, ground, incoming, 1.0 if dy < -20 else 0.0]

def main():
    path = sys.argv[1] if len(sys.argv) > 1 else sorted(glob.glob(os.path.join(GAME, "dion_hunted_tactics_*.txt")), key=os.path.getmtime)[-1]
    fields, net = load(path)
    print(os.path.basename(path))
    print("decisions %s  rewards %s  total %s  recent surprise %.2f  baseline %.2f" % (fields["d"], fields["r"], fields["t"], float(fields["rs"]), float(fields["bs"])))
    d = int(fields["d"])
    print("scheduled exploration %.0f%% (floor 5%%)" % (100 * max(0.05, 0.3 * 0.5 ** (d / 300))))
    print()
    rows = [
        ("player level, 60 px, armed", situation(-60, 0)),
        ("player level, 200 px, armed", situation(-200, 0)),
        ("player level, 400 px, armed", situation(-400, 0)),
        ("player level, 200 px, unarmed", situation(-200, 0, armed=0)),
        ("player above on a beam, 130 px", situation(-80, 100)),
        ("player above, 250 px", situation(-150, 200)),
        ("player below, 200 px", situation(-120, -160)),
        ("player level, 200 px, no line of sight", situation(-200, 0, los=0, since=100)),
        ("player level 200 px, high threat", situation(-200, 0, threat=0.8)),
        ("player charging (fast), 100 px", situation(-100, 0, moving=1.0)),
        ("player level 300 px, spear incoming", situation(-300, 0, incoming=1)),
        ("player straight below, 120 px", situation(-20, -120)),
        ("player level 200 px, I am in the air", situation(-200, 0, ground=0)),
    ]
    print("%-42s " % "situation" + " ".join("%11s" % t for t in TACTICS) + "   pick")
    for name, x in rows:
        s = forward(net, x)
        best = max(range(len(TACTICS)), key=lambda i: s[i])
        print("%-42s " % name + " ".join("%11.2f" % v for v in s) + "   " + TACTICS[best])
    print()
    spread = max(abs(v) for _, x in rows for v in forward(net, x))
    print("largest estimate magnitude %.2f (a fresh, untrained net stays within about 0.3)" % spread)

if __name__ == "__main__":
    main()
