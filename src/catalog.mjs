export const CATALOG = [
  {
    "category": "static",
    "params": [
      {
        "key": "color",
        "label": "Color",
        "type": "color",
        "default": "#ffffff",
        "random_param": "random_color",
        "brightness_param": "brightness_color"
      },
      {
        "key": "random_color",
        "label": "Randomize color",
        "type": "bool",
        "default": false,
        "randomizes": "color"
      },
      {
        "key": "brightness_color",
        "label": "Color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color"
      },
      {
        "key": "color_transition",
        "label": "Color transition",
        "type": "float",
        "default": 8.0,
        "min": 1.0,
        "max": 30.0,
        "step": 0.5,
        "unit": "s"
      }
    ],
    "id": "static",
    "name": "Static",
    "description": "One color on every port."
  },
  {
    "category": "animated",
    "params": [
      {
        "key": "color",
        "label": "Color",
        "type": "color",
        "default": "#0a84ff",
        "random_param": "random_color",
        "brightness_param": "brightness_color"
      },
      {
        "key": "playback",
        "label": "Playback",
        "type": "choice",
        "default": "custom",
        "choices": [
          "synchronized",
          "custom"
        ]
      },
      {
        "key": "pace",
        "label": "Shared speed",
        "type": "int",
        "default": 2,
        "min": 1,
        "max": 3,
        "step": 1
      },
      {
        "key": "period",
        "label": "Breath length",
        "type": "float",
        "default": 5.0,
        "min": 1.0,
        "max": 30.0,
        "step": 0.5,
        "unit": "s"
      },
      {
        "key": "floor",
        "label": "Lowest brightness",
        "type": "float",
        "default": 0.1,
        "min": 0.0,
        "max": 1.0,
        "step": 0.05
      },
      {
        "key": "ripple",
        "label": "Ripple across ports",
        "type": "bool",
        "default": false
      },
      {
        "key": "random_color",
        "label": "Randomize color",
        "type": "bool",
        "default": false,
        "randomizes": "color"
      },
      {
        "key": "brightness_color",
        "label": "Color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color"
      },
      {
        "key": "color_transition",
        "label": "Color transition",
        "type": "float",
        "default": 8.0,
        "min": 1.0,
        "max": 30.0,
        "step": 0.5,
        "unit": "s"
      }
    ],
    "id": "breathing",
    "name": "Breathing",
    "description": "Fades in and out."
  },
  {
    "category": "animated",
    "params": [
      {
        "key": "speed",
        "label": "Speed",
        "type": "float",
        "default": 0.05,
        "min": 0.01,
        "max": 1.0,
        "step": 0.01,
        "unit": "cycles/s"
      },
      {
        "key": "saturation",
        "label": "Saturation",
        "type": "float",
        "default": 1.0,
        "min": 0.0,
        "max": 1.0,
        "step": 0.05
      }
    ],
    "id": "color_cycle",
    "name": "Color Cycle",
    "description": "All ports move through the color wheel together."
  },
  {
    "category": "animated",
    "params": [
      {
        "key": "speed",
        "label": "Speed",
        "type": "float",
        "default": 0.1,
        "min": 0.0,
        "max": 1.0,
        "step": 0.01,
        "unit": "cycles/s"
      },
      {
        "key": "spread",
        "label": "Spread",
        "type": "float",
        "default": 1.0,
        "min": 0.1,
        "max": 3.0,
        "step": 0.05,
        "description": "How much of the color wheel fits across the ports."
      },
      {
        "key": "saturation",
        "label": "Saturation",
        "type": "float",
        "default": 1.0,
        "min": 0.0,
        "max": 1.0,
        "step": 0.05
      },
      {
        "key": "reverse",
        "label": "Reverse",
        "type": "bool",
        "default": false
      }
    ],
    "id": "rainbow",
    "name": "Rainbow Wave",
    "description": "A rainbow that moves along the ports."
  },
  {
    "category": "animated",
    "params": [
      {
        "key": "color",
        "label": "Color",
        "type": "color",
        "default": "#64d2ff",
        "random_param": "random_color",
        "brightness_param": "brightness_color"
      },
      {
        "key": "background",
        "label": "Background",
        "type": "color",
        "default": "#000000",
        "random_param": "random_background",
        "brightness_param": "brightness_background"
      },
      {
        "key": "speed",
        "label": "Speed",
        "type": "float",
        "default": 1.0,
        "min": 0.25,
        "max": 12.0,
        "step": 0.25,
        "unit": "ports/s"
      },
      {
        "key": "tail",
        "label": "Tail",
        "type": "float",
        "default": 3.0,
        "min": 0.5,
        "max": 8.0,
        "step": 0.5,
        "unit": "ports"
      },
      {
        "key": "bounce",
        "label": "Bounce",
        "type": "bool",
        "default": false
      },
      {
        "key": "random_color",
        "label": "Randomize color",
        "type": "bool",
        "default": false,
        "randomizes": "color"
      },
      {
        "key": "random_background",
        "label": "Randomize background",
        "type": "bool",
        "default": false,
        "randomizes": "background"
      },
      {
        "key": "brightness_color",
        "label": "Color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color"
      },
      {
        "key": "brightness_background",
        "label": "Background brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "background"
      },
      {
        "key": "color_transition",
        "label": "Color transition",
        "type": "float",
        "default": 8.0,
        "min": 1.0,
        "max": 30.0,
        "step": 0.5,
        "unit": "s"
      }
    ],
    "id": "comet",
    "name": "Comet",
    "description": "A bright point with a fading tail that moves along the ports."
  },
  {
    "category": "animated",
    "params": [
      {
        "key": "color",
        "label": "Color",
        "type": "color",
        "default": "#ff453a",
        "random_param": "random_color",
        "brightness_param": "brightness_color"
      },
      {
        "key": "background",
        "label": "Background",
        "type": "color",
        "default": "#000000",
        "random_param": "random_background",
        "brightness_param": "brightness_background"
      },
      {
        "key": "speed",
        "label": "Speed",
        "type": "float",
        "default": 1.0,
        "min": 0.25,
        "max": 12.0,
        "step": 0.25,
        "unit": "ports/s"
      },
      {
        "key": "width",
        "label": "Width",
        "type": "float",
        "default": 2.0,
        "min": 2.0,
        "max": 4.0,
        "step": 0.1,
        "unit": "ports"
      },
      {
        "key": "random_color",
        "label": "Randomize color",
        "type": "bool",
        "default": false,
        "randomizes": "color"
      },
      {
        "key": "random_background",
        "label": "Randomize background",
        "type": "bool",
        "default": false,
        "randomizes": "background"
      },
      {
        "key": "brightness_color",
        "label": "Color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color"
      },
      {
        "key": "brightness_background",
        "label": "Background brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "background"
      },
      {
        "key": "color_transition",
        "label": "Color transition",
        "type": "float",
        "default": 8.0,
        "min": 1.0,
        "max": 30.0,
        "step": 0.5,
        "unit": "s"
      }
    ],
    "id": "scanner",
    "name": "Scanner",
    "description": "A light that sweeps back and forth."
  },
  {
    "category": "animated",
    "params": [
      {
        "key": "color",
        "label": "Color",
        "type": "color",
        "default": "#ffd60a",
        "random_param": "random_color",
        "brightness_param": "brightness_color"
      },
      {
        "key": "background",
        "label": "Background",
        "type": "color",
        "default": "#000000",
        "random_param": "random_background",
        "brightness_param": "brightness_background"
      },
      {
        "key": "speed",
        "label": "Speed",
        "type": "float",
        "default": 0.75,
        "min": 0.25,
        "max": 10.0,
        "step": 0.25,
        "unit": "ports/s"
      },
      {
        "key": "spacing",
        "label": "Spacing",
        "type": "int",
        "default": 3,
        "min": 2,
        "max": 4,
        "step": 1,
        "unit": "ports"
      },
      {
        "key": "random_color",
        "label": "Randomize color",
        "type": "bool",
        "default": false,
        "randomizes": "color"
      },
      {
        "key": "random_background",
        "label": "Randomize background",
        "type": "bool",
        "default": false,
        "randomizes": "background"
      },
      {
        "key": "brightness_color",
        "label": "Color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color"
      },
      {
        "key": "brightness_background",
        "label": "Background brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "background"
      },
      {
        "key": "color_transition",
        "label": "Color transition",
        "type": "float",
        "default": 8.0,
        "min": 1.0,
        "max": 30.0,
        "step": 0.5,
        "unit": "s"
      }
    ],
    "id": "marquee",
    "name": "Marquee",
    "description": "Every second, third or fourth port lit, stepping along."
  },
  {
    "category": "animated",
    "params": [
      {
        "key": "color",
        "label": "Color",
        "type": "color",
        "default": "#ffffff",
        "random_param": "random_color",
        "brightness_param": "brightness_color"
      },
      {
        "key": "background",
        "label": "Background",
        "type": "color",
        "default": "#000000",
        "random_param": "random_background",
        "brightness_param": "brightness_background"
      },
      {
        "key": "rainbow",
        "label": "Different color per sparkle",
        "type": "bool",
        "default": false
      },
      {
        "key": "rate",
        "label": "Sparkles",
        "type": "float",
        "default": 0.3,
        "min": 0.05,
        "max": 2.0,
        "step": 0.05,
        "unit": "per port/s"
      },
      {
        "key": "fade",
        "label": "Fade",
        "type": "float",
        "default": 2.5,
        "min": 1.5,
        "max": 6.0,
        "step": 0.1,
        "unit": "s"
      },
      {
        "key": "random_color",
        "label": "Randomize color",
        "type": "bool",
        "default": false,
        "randomizes": "color"
      },
      {
        "key": "random_background",
        "label": "Randomize background",
        "type": "bool",
        "default": false,
        "randomizes": "background"
      },
      {
        "key": "brightness_color",
        "label": "Color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color"
      },
      {
        "key": "brightness_background",
        "label": "Background brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "background"
      },
      {
        "key": "color_transition",
        "label": "Color transition",
        "type": "float",
        "default": 8.0,
        "min": 1.0,
        "max": 30.0,
        "step": 0.5,
        "unit": "s"
      }
    ],
    "id": "starry_night",
    "name": "Starry Night",
    "description": "Random ports light up and fade out.",
    "exclusive_params": [
      [
        "random_color",
        "rainbow"
      ]
    ]
  },
  {
    "category": "animated",
    "params": [
      {
        "key": "palette",
        "label": "Palette",
        "type": "choice",
        "default": "fire",
        "choices": [
          "fire",
          "ice",
          "toxic",
          "plasma"
        ]
      },
      {
        "key": "intensity",
        "label": "Intensity",
        "type": "float",
        "default": 0.85,
        "min": 0.1,
        "max": 1.0,
        "step": 0.05
      },
      {
        "key": "speed",
        "label": "Flicker",
        "type": "float",
        "default": 1.0,
        "min": 0.1,
        "max": 3.0,
        "step": 0.1
      }
    ],
    "id": "fire",
    "name": "Fire",
    "description": "Each port flickers on its own."
  },
  {
    "category": "animated",
    "params": [
      {
        "key": "color_a",
        "label": "Left color",
        "type": "color",
        "default": "#ff0000",
        "random_param": "random_color_a",
        "brightness_param": "brightness_color_a"
      },
      {
        "key": "color_b",
        "label": "Right color",
        "type": "color",
        "default": "#0040ff",
        "random_param": "random_color_b",
        "brightness_param": "brightness_color_b"
      },
      {
        "key": "speed",
        "label": "Speed",
        "type": "float",
        "default": 0.4,
        "min": 0.1,
        "max": 3.0,
        "step": 0.05,
        "unit": "cycles/s"
      },
      {
        "key": "pattern",
        "label": "Pattern",
        "type": "choice",
        "default": "split",
        "choices": [
          "split",
          "alternate",
          "together"
        ]
      },
      {
        "key": "random_color_a",
        "label": "Randomize left color",
        "type": "bool",
        "default": false,
        "randomizes": "color_a"
      },
      {
        "key": "random_color_b",
        "label": "Randomize right color",
        "type": "bool",
        "default": false,
        "randomizes": "color_b"
      },
      {
        "key": "brightness_color_a",
        "label": "Left color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color_a"
      },
      {
        "key": "brightness_color_b",
        "label": "Right color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color_b"
      },
      {
        "key": "color_transition",
        "label": "Color transition",
        "type": "float",
        "default": 8.0,
        "min": 1.0,
        "max": 30.0,
        "step": 0.5,
        "unit": "s"
      }
    ],
    "id": "police",
    "name": "Police",
    "description": "Alternating flashes in two colors."
  },
  {
    "category": "animated",
    "params": [
      {
        "key": "color",
        "label": "Color",
        "type": "color",
        "default": "#ffffff",
        "random_param": "random_color",
        "brightness_param": "brightness_color"
      },
      {
        "key": "rate",
        "label": "Rate",
        "type": "float",
        "default": 0.5,
        "min": 0.25,
        "max": 4.0,
        "step": 0.25,
        "unit": "Hz"
      },
      {
        "key": "duty",
        "label": "On time",
        "type": "float",
        "default": 0.5,
        "min": 0.4,
        "max": 0.6,
        "step": 0.05
      },
      {
        "key": "random_colors",
        "label": "New color each flash",
        "type": "bool",
        "default": false
      },
      {
        "key": "random_color",
        "label": "Randomize color",
        "type": "bool",
        "default": false,
        "randomizes": "color"
      },
      {
        "key": "brightness_color",
        "label": "Color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color"
      },
      {
        "key": "color_transition",
        "label": "Color transition",
        "type": "float",
        "default": 8.0,
        "min": 1.0,
        "max": 30.0,
        "step": 0.5,
        "unit": "s"
      }
    ],
    "id": "strobe",
    "name": "Strobe",
    "exclusive_params": [
      [
        "random_color",
        "random_colors"
      ]
    ],
    "description": "Fast flashes. Can affect people who are sensitive to flashing light."
  },
  {
    "category": "animated",
    "params": [
      {
        "key": "color_a",
        "label": "From",
        "type": "color",
        "default": "#79dcc0",
        "random_param": "random_color_a",
        "brightness_param": "brightness_color_a"
      },
      {
        "key": "color_b",
        "label": "To",
        "type": "color",
        "default": "#a38de0",
        "random_param": "random_color_b",
        "brightness_param": "brightness_color_b"
      },
      {
        "key": "speed",
        "label": "Speed",
        "type": "float",
        "default": 0.0,
        "min": 0.0,
        "max": 3.0,
        "step": 0.01,
        "unit": "cycles/s",
        "description": "0 keeps the gradient still."
      },
      {
        "key": "mirror",
        "label": "Mirror",
        "type": "bool",
        "default": true,
        "description": "Use an even back-and-forth blend instead of a longer sweep and shorter return."
      },
      {
        "key": "random_color_a",
        "label": "Randomize from",
        "type": "bool",
        "default": false,
        "randomizes": "color_a"
      },
      {
        "key": "random_color_b",
        "label": "Randomize to",
        "type": "bool",
        "default": false,
        "randomizes": "color_b"
      },
      {
        "key": "brightness_color_a",
        "label": "From brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color_a"
      },
      {
        "key": "brightness_color_b",
        "label": "To brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color_b"
      },
      {
        "key": "color_transition",
        "label": "Color transition",
        "type": "float",
        "default": 8.0,
        "min": 1.0,
        "max": 30.0,
        "step": 0.5,
        "unit": "s"
      }
    ],
    "id": "gradient",
    "name": "Gradient",
    "description": "A blend between two colors, static or moving."
  },
  {
    "category": "animated",
    "params": [
      {
        "key": "color",
        "label": "Color",
        "type": "color",
        "default": "#64d2ff",
        "random_param": "random_color",
        "brightness_param": "brightness_color"
      },
      {
        "key": "background",
        "label": "Background",
        "type": "color",
        "default": "#000000",
        "random_param": "random_background",
        "brightness_param": "brightness_background"
      },
      {
        "key": "center",
        "label": "Center port",
        "type": "int",
        "default": 0,
        "min": 0,
        "max": 48,
        "step": 1,
        "description": "0 = the middle."
      },
      {
        "key": "speed",
        "label": "Speed",
        "type": "float",
        "default": 1.0,
        "min": 0.25,
        "max": 10.0,
        "step": 0.25,
        "unit": "ports/s"
      },
      {
        "key": "spacing",
        "label": "Ring spacing",
        "type": "float",
        "default": 4.0,
        "min": 2.0,
        "max": 12.0,
        "step": 0.5,
        "unit": "ports"
      },
      {
        "key": "random_color",
        "label": "Randomize color",
        "type": "bool",
        "default": false,
        "randomizes": "color"
      },
      {
        "key": "random_background",
        "label": "Randomize background",
        "type": "bool",
        "default": false,
        "randomizes": "background"
      },
      {
        "key": "brightness_color",
        "label": "Color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color"
      },
      {
        "key": "brightness_background",
        "label": "Background brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "background"
      },
      {
        "key": "color_transition",
        "label": "Color transition",
        "type": "float",
        "default": 8.0,
        "min": 1.0,
        "max": 30.0,
        "step": 0.5,
        "unit": "s"
      }
    ],
    "id": "ripple",
    "name": "Ripple",
    "description": "Rings that move outward from one port."
  },
  {
    "category": "animated",
    "params": [
      {
        "key": "color",
        "label": "Flash",
        "type": "color",
        "default": "#e8f0ff",
        "random_param": "random_color",
        "brightness_param": "brightness_color"
      },
      {
        "key": "sky",
        "label": "Sky",
        "type": "color",
        "default": "#050a24",
        "random_param": "random_sky",
        "brightness_param": "brightness_sky"
      },
      {
        "key": "rate",
        "label": "Strikes",
        "type": "float",
        "default": 0.3,
        "min": 0.05,
        "max": 2.0,
        "step": 0.05,
        "unit": "per s"
      },
      {
        "key": "reach",
        "label": "Reach",
        "type": "int",
        "default": 2,
        "min": 0,
        "max": 4,
        "step": 1,
        "unit": "ports",
        "description": "How many neighboring ports a flash spreads to."
      },
      {
        "key": "random_color",
        "label": "Randomize flash",
        "type": "bool",
        "default": false,
        "randomizes": "color"
      },
      {
        "key": "random_sky",
        "label": "Randomize sky",
        "type": "bool",
        "default": false,
        "randomizes": "sky"
      },
      {
        "key": "brightness_color",
        "label": "Flash brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color"
      },
      {
        "key": "brightness_sky",
        "label": "Sky brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "sky"
      },
      {
        "key": "color_transition",
        "label": "Color transition",
        "type": "float",
        "default": 8.0,
        "min": 1.0,
        "max": 30.0,
        "step": 0.5,
        "unit": "s"
      }
    ],
    "id": "lightning",
    "name": "Lightning",
    "description": "A dark background with random double flashes."
  },
  {
    "category": "time",
    "params": [
      {
        "key": "palette",
        "label": "Palette",
        "type": "choice",
        "default": "natural",
        "choices": [
          "natural",
          "warm",
          "vivid"
        ]
      },
      {
        "key": "night_brightness",
        "label": "Night brightness",
        "type": "float",
        "default": 0.35,
        "min": 0.0,
        "max": 1.0,
        "step": 0.05
      }
    ],
    "id": "daylight",
    "name": "Daylight",
    "description": "Follows the clock through sunrise, day, sunset and night colors."
  },
  {
    "category": "data",
    "params": [
      {
        "key": "idle_color",
        "label": "Idle",
        "type": "color",
        "default": "#0a3cff",
        "random_param": "random_idle_color",
        "brightness_param": "brightness_idle_color"
      },
      {
        "key": "busy_color",
        "label": "Busy",
        "type": "color",
        "default": "#ff3b30",
        "random_param": "random_busy_color",
        "brightness_param": "brightness_busy_color"
      },
      {
        "key": "smoothing",
        "label": "Smoothing",
        "type": "float",
        "default": 0.6,
        "min": 0.0,
        "max": 0.95,
        "step": 0.05
      },
      {
        "key": "offline_color",
        "label": "No link",
        "type": "color",
        "default": "#000000",
        "random_param": "random_offline_color",
        "brightness_param": "brightness_offline_color"
      },
      {
        "key": "custom_uplink",
        "label": "Customize uplink",
        "type": "bool",
        "default": false
      },
      {
        "key": "uplink_color",
        "label": "Uplink",
        "type": "color",
        "default": "#ffffff",
        "random_param": "random_uplink_color",
        "brightness_param": "brightness_uplink_color"
      },
      {
        "key": "random_idle_color",
        "label": "Randomize idle",
        "type": "bool",
        "default": false,
        "randomizes": "idle_color"
      },
      {
        "key": "random_busy_color",
        "label": "Randomize busy",
        "type": "bool",
        "default": false,
        "randomizes": "busy_color"
      },
      {
        "key": "random_offline_color",
        "label": "Randomize no link",
        "type": "bool",
        "default": false,
        "randomizes": "offline_color"
      },
      {
        "key": "random_uplink_color",
        "label": "Randomize uplink",
        "type": "bool",
        "default": false,
        "randomizes": "uplink_color"
      },
      {
        "key": "brightness_idle_color",
        "label": "Idle brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "idle_color"
      },
      {
        "key": "brightness_busy_color",
        "label": "Busy brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "busy_color"
      },
      {
        "key": "brightness_offline_color",
        "label": "No link brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "offline_color"
      },
      {
        "key": "brightness_uplink_color",
        "label": "Uplink brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "uplink_color"
      },
      {
        "key": "color_transition",
        "label": "Color transition",
        "type": "float",
        "default": 8.0,
        "min": 1.0,
        "max": 30.0,
        "step": 0.5,
        "unit": "s"
      }
    ],
    "id": "traffic",
    "name": "Live Traffic",
    "description": "Colors follow each device\u2019s traffic, scaled automatically to its link speed."
  },
  {
    "category": "animated",
    "params": [
      {
        "key": "color_a",
        "label": "First curtain",
        "type": "color",
        "default": "#38e8b5",
        "random_param": "random_color_a",
        "brightness_param": "brightness_color_a"
      },
      {
        "key": "color_b",
        "label": "Second curtain",
        "type": "color",
        "default": "#ae78ff",
        "random_param": "random_color_b",
        "brightness_param": "brightness_color_b"
      },
      {
        "key": "background",
        "label": "Background",
        "type": "color",
        "default": "#07142e",
        "random_param": "random_background",
        "brightness_param": "brightness_background"
      },
      {
        "key": "speed",
        "label": "Speed",
        "type": "float",
        "default": 0.08,
        "min": 0.02,
        "max": 1.0,
        "step": 0.01,
        "unit": "cycles/s"
      },
      {
        "key": "layers",
        "label": "Depth",
        "type": "float",
        "default": 0.65,
        "min": 0.1,
        "max": 1.0,
        "step": 0.05
      },
      {
        "key": "random_color_a",
        "label": "Randomize first curtain",
        "type": "bool",
        "default": false,
        "randomizes": "color_a"
      },
      {
        "key": "random_color_b",
        "label": "Randomize second curtain",
        "type": "bool",
        "default": false,
        "randomizes": "color_b"
      },
      {
        "key": "random_background",
        "label": "Randomize background",
        "type": "bool",
        "default": false,
        "randomizes": "background"
      },
      {
        "key": "brightness_color_a",
        "label": "First curtain brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color_a"
      },
      {
        "key": "brightness_color_b",
        "label": "Second curtain brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color_b"
      },
      {
        "key": "brightness_background",
        "label": "Background brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "background"
      },
      {
        "key": "color_transition",
        "label": "Color transition",
        "type": "float",
        "default": 8.0,
        "min": 1.0,
        "max": 30.0,
        "step": 0.5,
        "unit": "s"
      }
    ],
    "id": "aurora",
    "name": "Aurora",
    "description": "Overlapping curtains of color drift across the ports."
  },
  {
    "category": "animated",
    "params": [
      {
        "key": "color_a",
        "label": "First color",
        "type": "color",
        "default": "#fa6399",
        "random_param": "random_color_a",
        "brightness_param": "brightness_color_a"
      },
      {
        "key": "color_b",
        "label": "Second color",
        "type": "color",
        "default": "#ffbe63",
        "random_param": "random_color_b",
        "brightness_param": "brightness_color_b"
      },
      {
        "key": "background",
        "label": "Background",
        "type": "color",
        "default": "#200c38",
        "random_param": "random_background",
        "brightness_param": "brightness_background"
      },
      {
        "key": "speed",
        "label": "Speed",
        "type": "float",
        "default": 0.06,
        "min": 0.01,
        "max": 0.8,
        "step": 0.01,
        "unit": "cycles/s"
      },
      {
        "key": "width",
        "label": "Blob width",
        "type": "float",
        "default": 2.5,
        "min": 1.25,
        "max": 8.0,
        "step": 0.25,
        "unit": "ports"
      },
      {
        "key": "random_color_a",
        "label": "Randomize first color",
        "type": "bool",
        "default": false,
        "randomizes": "color_a"
      },
      {
        "key": "random_color_b",
        "label": "Randomize second color",
        "type": "bool",
        "default": false,
        "randomizes": "color_b"
      },
      {
        "key": "random_background",
        "label": "Randomize background",
        "type": "bool",
        "default": false,
        "randomizes": "background"
      },
      {
        "key": "brightness_color_a",
        "label": "First color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color_a"
      },
      {
        "key": "brightness_color_b",
        "label": "Second color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color_b"
      },
      {
        "key": "brightness_background",
        "label": "Background brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "background"
      },
      {
        "key": "color_transition",
        "label": "Color transition",
        "type": "float",
        "default": 8.0,
        "min": 1.0,
        "max": 30.0,
        "step": 0.5,
        "unit": "s"
      }
    ],
    "id": "lava_lamp",
    "name": "Lava Lamp",
    "description": "Soft blobs of color drift, stretch and merge."
  },
  {
    "category": "animated",
    "params": [
      {
        "key": "color_a",
        "label": "First color",
        "type": "color",
        "default": "#5b4dff",
        "random_param": "random_color_a",
        "brightness_param": "brightness_color_a"
      },
      {
        "key": "color_b",
        "label": "Middle color",
        "type": "color",
        "default": "#ef60d0",
        "random_param": "random_color_b",
        "brightness_param": "brightness_color_b"
      },
      {
        "key": "color_c",
        "label": "Last color",
        "type": "color",
        "default": "#61e9e2",
        "random_param": "random_color_c",
        "brightness_param": "brightness_color_c"
      },
      {
        "key": "speed",
        "label": "Speed",
        "type": "float",
        "default": 0.1,
        "min": 0.02,
        "max": 1.5,
        "step": 0.01,
        "unit": "cycles/s"
      },
      {
        "key": "scale",
        "label": "Wave density",
        "type": "float",
        "default": 1.2,
        "min": 0.5,
        "max": 3.0,
        "step": 0.1
      },
      {
        "key": "random_color_a",
        "label": "Randomize first color",
        "type": "bool",
        "default": false,
        "randomizes": "color_a"
      },
      {
        "key": "random_color_b",
        "label": "Randomize middle color",
        "type": "bool",
        "default": false,
        "randomizes": "color_b"
      },
      {
        "key": "random_color_c",
        "label": "Randomize last color",
        "type": "bool",
        "default": false,
        "randomizes": "color_c"
      },
      {
        "key": "brightness_color_a",
        "label": "First color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color_a"
      },
      {
        "key": "brightness_color_b",
        "label": "Middle color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color_b"
      },
      {
        "key": "brightness_color_c",
        "label": "Last color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color_c"
      },
      {
        "key": "color_transition",
        "label": "Color transition",
        "type": "float",
        "default": 8.0,
        "min": 1.0,
        "max": 30.0,
        "step": 0.5,
        "unit": "s"
      }
    ],
    "id": "plasma",
    "name": "Plasma",
    "description": "Intersecting waves reshape a flowing three-color field."
  },
  {
    "category": "animated",
    "params": [
      {
        "key": "deep_color",
        "label": "Deep water",
        "type": "color",
        "default": "#0843a8",
        "random_param": "random_deep_color",
        "brightness_param": "brightness_deep_color"
      },
      {
        "key": "crest_color",
        "label": "Wave crest",
        "type": "color",
        "default": "#56e1d2",
        "random_param": "random_crest_color",
        "brightness_param": "brightness_crest_color"
      },
      {
        "key": "foam_color",
        "label": "Highlight",
        "type": "color",
        "default": "#c0fff2",
        "random_param": "random_foam_color",
        "brightness_param": "brightness_foam_color"
      },
      {
        "key": "speed",
        "label": "Speed",
        "type": "float",
        "default": 0.09,
        "min": 0.02,
        "max": 1.0,
        "step": 0.01,
        "unit": "cycles/s"
      },
      {
        "key": "depth",
        "label": "Swell depth",
        "type": "float",
        "default": 0.6,
        "min": 0.1,
        "max": 1.0,
        "step": 0.05
      },
      {
        "key": "random_deep_color",
        "label": "Randomize deep water",
        "type": "bool",
        "default": false,
        "randomizes": "deep_color"
      },
      {
        "key": "random_crest_color",
        "label": "Randomize wave crest",
        "type": "bool",
        "default": false,
        "randomizes": "crest_color"
      },
      {
        "key": "random_foam_color",
        "label": "Randomize highlight",
        "type": "bool",
        "default": false,
        "randomizes": "foam_color"
      },
      {
        "key": "brightness_deep_color",
        "label": "Deep water brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "deep_color"
      },
      {
        "key": "brightness_crest_color",
        "label": "Wave crest brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "crest_color"
      },
      {
        "key": "brightness_foam_color",
        "label": "Highlight brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "foam_color"
      },
      {
        "key": "color_transition",
        "label": "Color transition",
        "type": "float",
        "default": 8.0,
        "min": 1.0,
        "max": 30.0,
        "step": 0.5,
        "unit": "s"
      }
    ],
    "id": "ocean_swell",
    "name": "Ocean Swell",
    "description": "Layered swells rise into bright crests and settle back."
  },
  {
    "category": "animated",
    "params": [
      {
        "key": "color",
        "label": "Base color",
        "type": "color",
        "default": "#f5c789",
        "random_param": "random_color",
        "brightness_param": "brightness_color"
      },
      {
        "key": "shadow",
        "label": "Shadow color",
        "type": "color",
        "default": "#0e1539",
        "random_param": "random_shadow",
        "brightness_param": "brightness_shadow"
      },
      {
        "key": "speed",
        "label": "Speed",
        "type": "float",
        "default": 0.1,
        "min": 0.02,
        "max": 1.5,
        "step": 0.01,
        "unit": "cycles/s"
      },
      {
        "key": "width",
        "label": "Shadow width",
        "type": "float",
        "default": 2.0,
        "min": 1.25,
        "max": 8.0,
        "step": 0.25,
        "unit": "ports"
      },
      {
        "key": "depth",
        "label": "Shadow depth",
        "type": "float",
        "default": 0.9,
        "min": 0.1,
        "max": 1.0,
        "step": 0.05
      },
      {
        "key": "random_color",
        "label": "Randomize base color",
        "type": "bool",
        "default": false,
        "randomizes": "color"
      },
      {
        "key": "random_shadow",
        "label": "Randomize shadow color",
        "type": "bool",
        "default": false,
        "randomizes": "shadow"
      },
      {
        "key": "brightness_color",
        "label": "Base color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color"
      },
      {
        "key": "brightness_shadow",
        "label": "Shadow color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "shadow"
      },
      {
        "key": "color_transition",
        "label": "Color transition",
        "type": "float",
        "default": 8.0,
        "min": 1.0,
        "max": 30.0,
        "step": 0.5,
        "unit": "s"
      }
    ],
    "id": "eclipse",
    "name": "Eclipse",
    "description": "A soft shadow passes over a steady field of color."
  },
  {
    "category": "animated",
    "params": [
      {
        "key": "color_a",
        "label": "First streak",
        "type": "color",
        "default": "#7aa6ff",
        "random_param": "random_color_a",
        "brightness_param": "brightness_color_a"
      },
      {
        "key": "color_b",
        "label": "Second streak",
        "type": "color",
        "default": "#d5b1ff",
        "random_param": "random_color_b",
        "brightness_param": "brightness_color_b"
      },
      {
        "key": "background",
        "label": "Background",
        "type": "color",
        "default": "#04091e",
        "random_param": "random_background",
        "brightness_param": "brightness_background"
      },
      {
        "key": "speed",
        "label": "Speed",
        "type": "float",
        "default": 0.12,
        "min": 0.03,
        "max": 1.5,
        "step": 0.01,
        "unit": "cycles/s"
      },
      {
        "key": "count",
        "label": "Streaks",
        "type": "int",
        "default": 3,
        "min": 1,
        "max": 4,
        "step": 1
      },
      {
        "key": "tail",
        "label": "Trail length",
        "type": "float",
        "default": 3.0,
        "min": 1.25,
        "max": 8.0,
        "step": 0.25,
        "unit": "ports"
      },
      {
        "key": "random_color_a",
        "label": "Randomize first streak",
        "type": "bool",
        "default": false,
        "randomizes": "color_a"
      },
      {
        "key": "random_color_b",
        "label": "Randomize second streak",
        "type": "bool",
        "default": false,
        "randomizes": "color_b"
      },
      {
        "key": "random_background",
        "label": "Randomize background",
        "type": "bool",
        "default": false,
        "randomizes": "background"
      },
      {
        "key": "brightness_color_a",
        "label": "First streak brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color_a"
      },
      {
        "key": "brightness_color_b",
        "label": "Second streak brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color_b"
      },
      {
        "key": "brightness_background",
        "label": "Background brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "background"
      },
      {
        "key": "color_transition",
        "label": "Color transition",
        "type": "float",
        "default": 8.0,
        "min": 1.0,
        "max": 30.0,
        "step": 0.5,
        "unit": "s"
      }
    ],
    "id": "meteor_shower",
    "name": "Meteor Shower",
    "description": "Several soft streaks cross the ports with fading trails."
  },
  {
    "category": "animated",
    "params": [
      {
        "key": "color_a",
        "label": "Forward color",
        "type": "color",
        "default": "#fa8cba",
        "random_param": "random_color_a",
        "brightness_param": "brightness_color_a"
      },
      {
        "key": "color_b",
        "label": "Reverse color",
        "type": "color",
        "default": "#5dcfdc",
        "random_param": "random_color_b",
        "brightness_param": "brightness_color_b"
      },
      {
        "key": "background",
        "label": "Background",
        "type": "color",
        "default": "#071323",
        "random_param": "random_background",
        "brightness_param": "brightness_background"
      },
      {
        "key": "speed",
        "label": "Speed",
        "type": "float",
        "default": 0.1,
        "min": 0.02,
        "max": 1.5,
        "step": 0.01,
        "unit": "cycles/s"
      },
      {
        "key": "width",
        "label": "Stream width",
        "type": "float",
        "default": 2.25,
        "min": 1.25,
        "max": 8.0,
        "step": 0.25,
        "unit": "ports"
      },
      {
        "key": "random_color_a",
        "label": "Randomize forward color",
        "type": "bool",
        "default": false,
        "randomizes": "color_a"
      },
      {
        "key": "random_color_b",
        "label": "Randomize reverse color",
        "type": "bool",
        "default": false,
        "randomizes": "color_b"
      },
      {
        "key": "random_background",
        "label": "Randomize background",
        "type": "bool",
        "default": false,
        "randomizes": "background"
      },
      {
        "key": "brightness_color_a",
        "label": "Forward color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color_a"
      },
      {
        "key": "brightness_color_b",
        "label": "Reverse color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color_b"
      },
      {
        "key": "brightness_background",
        "label": "Background brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "background"
      },
      {
        "key": "color_transition",
        "label": "Color transition",
        "type": "float",
        "default": 8.0,
        "min": 1.0,
        "max": 30.0,
        "step": 0.5,
        "unit": "s"
      }
    ],
    "id": "counterflow",
    "name": "Counterflow",
    "description": "Two streams travel in opposite directions and blend as they meet."
  },
  {
    "category": "animated",
    "params": [
      {
        "key": "color_a",
        "label": "First color",
        "type": "color",
        "default": "#f0a3c2",
        "random_param": "random_color_a",
        "brightness_param": "brightness_color_a"
      },
      {
        "key": "color_b",
        "label": "Second color",
        "type": "color",
        "default": "#9c9bea",
        "random_param": "random_color_b",
        "brightness_param": "brightness_color_b"
      },
      {
        "key": "color_c",
        "label": "Third color",
        "type": "color",
        "default": "#67d8bd",
        "random_param": "random_color_c",
        "brightness_param": "brightness_color_c"
      },
      {
        "key": "speed",
        "label": "Speed",
        "type": "float",
        "default": 0.12,
        "min": 0.02,
        "max": 1.5,
        "step": 0.01,
        "unit": "cycles/s"
      },
      {
        "key": "stagger",
        "label": "Stagger",
        "type": "float",
        "default": 0.6,
        "min": 0.0,
        "max": 0.9,
        "step": 0.05
      },
      {
        "key": "random_color_a",
        "label": "Randomize first color",
        "type": "bool",
        "default": false,
        "randomizes": "color_a"
      },
      {
        "key": "random_color_b",
        "label": "Randomize second color",
        "type": "bool",
        "default": false,
        "randomizes": "color_b"
      },
      {
        "key": "random_color_c",
        "label": "Randomize third color",
        "type": "bool",
        "default": false,
        "randomizes": "color_c"
      },
      {
        "key": "brightness_color_a",
        "label": "First color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color_a"
      },
      {
        "key": "brightness_color_b",
        "label": "Second color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color_b"
      },
      {
        "key": "brightness_color_c",
        "label": "Third color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color_c"
      },
      {
        "key": "color_transition",
        "label": "Color transition",
        "type": "float",
        "default": 8.0,
        "min": 1.0,
        "max": 30.0,
        "step": 0.5,
        "unit": "s"
      }
    ],
    "id": "color_dissolve",
    "name": "Color Dissolve",
    "description": "Ports blend through a palette in a staggered order."
  },
  {
    "category": "animated",
    "params": [
      {
        "key": "color_a",
        "label": "First color",
        "type": "color",
        "default": "#e56ba9",
        "random_param": "random_color_a",
        "brightness_param": "brightness_color_a"
      },
      {
        "key": "color_b",
        "label": "Second color",
        "type": "color",
        "default": "#768aef",
        "random_param": "random_color_b",
        "brightness_param": "brightness_color_b"
      },
      {
        "key": "color_c",
        "label": "Third color",
        "type": "color",
        "default": "#53d8c6",
        "random_param": "random_color_c",
        "brightness_param": "brightness_color_c"
      },
      {
        "key": "speed",
        "label": "Speed",
        "type": "float",
        "default": 0.1,
        "min": 0.02,
        "max": 1.0,
        "step": 0.01,
        "unit": "cycles/s"
      },
      {
        "key": "spread",
        "label": "Spread",
        "type": "float",
        "default": 0.55,
        "min": 0.1,
        "max": 0.8,
        "step": 0.05
      },
      {
        "key": "random_color_a",
        "label": "Randomize first color",
        "type": "bool",
        "default": false,
        "randomizes": "color_a"
      },
      {
        "key": "random_color_b",
        "label": "Randomize second color",
        "type": "bool",
        "default": false,
        "randomizes": "color_b"
      },
      {
        "key": "random_color_c",
        "label": "Randomize third color",
        "type": "bool",
        "default": false,
        "randomizes": "color_c"
      },
      {
        "key": "brightness_color_a",
        "label": "First color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color_a"
      },
      {
        "key": "brightness_color_b",
        "label": "Second color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color_b"
      },
      {
        "key": "brightness_color_c",
        "label": "Third color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color_c"
      },
      {
        "key": "color_transition",
        "label": "Color transition",
        "type": "float",
        "default": 8.0,
        "min": 1.0,
        "max": 30.0,
        "step": 0.5,
        "unit": "s"
      }
    ],
    "id": "ink_spread",
    "name": "Ink Spread",
    "description": "A new color spreads gently from a changing starting point."
  },
  {
    "category": "animated",
    "params": [
      {
        "key": "color_a",
        "label": "First color",
        "type": "color",
        "default": "#e7b678",
        "random_param": "random_color_a",
        "brightness_param": "brightness_color_a"
      },
      {
        "key": "color_b",
        "label": "Second color",
        "type": "color",
        "default": "#9d8be7",
        "random_param": "random_color_b",
        "brightness_param": "brightness_color_b"
      },
      {
        "key": "speed",
        "label": "Speed",
        "type": "float",
        "default": 0.12,
        "min": 0.02,
        "max": 1.5,
        "step": 0.01,
        "unit": "cycles/s"
      },
      {
        "key": "random_color_a",
        "label": "Randomize first color",
        "type": "bool",
        "default": false,
        "randomizes": "color_a"
      },
      {
        "key": "random_color_b",
        "label": "Randomize second color",
        "type": "bool",
        "default": false,
        "randomizes": "color_b"
      },
      {
        "key": "brightness_color_a",
        "label": "First color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color_a"
      },
      {
        "key": "brightness_color_b",
        "label": "Second color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color_b"
      },
      {
        "key": "color_transition",
        "label": "Color transition",
        "type": "float",
        "default": 8.0,
        "min": 1.0,
        "max": 30.0,
        "step": 0.5,
        "unit": "s"
      }
    ],
    "id": "alternating_fade",
    "name": "Alternating Fade",
    "description": "Odd and even ports crossfade between two colors."
  },
  {
    "category": "animated",
    "params": [
      {
        "key": "color",
        "label": "Tide color",
        "type": "color",
        "default": "#57dfc0",
        "random_param": "random_color",
        "brightness_param": "brightness_color"
      },
      {
        "key": "background",
        "label": "Background",
        "type": "color",
        "default": "#164099",
        "random_param": "random_background",
        "brightness_param": "brightness_background"
      },
      {
        "key": "speed",
        "label": "Speed",
        "type": "float",
        "default": 0.08,
        "min": 0.02,
        "max": 1.0,
        "step": 0.01,
        "unit": "cycles/s"
      },
      {
        "key": "width",
        "label": "Edge softness",
        "type": "float",
        "default": 4.0,
        "min": 1.25,
        "max": 8.0,
        "step": 0.25,
        "unit": "ports"
      },
      {
        "key": "reverse",
        "label": "Reverse",
        "type": "bool",
        "default": false
      },
      {
        "key": "random_color",
        "label": "Randomize tide color",
        "type": "bool",
        "default": false,
        "randomizes": "color"
      },
      {
        "key": "random_background",
        "label": "Randomize background",
        "type": "bool",
        "default": false,
        "randomizes": "background"
      },
      {
        "key": "brightness_color",
        "label": "Tide color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color"
      },
      {
        "key": "brightness_background",
        "label": "Background brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "background"
      },
      {
        "key": "color_transition",
        "label": "Color transition",
        "type": "float",
        "default": 8.0,
        "min": 1.0,
        "max": 30.0,
        "step": 0.5,
        "unit": "s"
      }
    ],
    "id": "color_tide",
    "name": "Color Tide",
    "description": "Color fills the row and gently recedes."
  },
  {
    "category": "animated",
    "params": [
      {
        "key": "first_start",
        "label": "First palette start",
        "type": "color",
        "default": "#ef948e",
        "random_param": "random_first_start",
        "brightness_param": "brightness_first_start"
      },
      {
        "key": "first_end",
        "label": "First palette end",
        "type": "color",
        "default": "#e5bf73",
        "random_param": "random_first_end",
        "brightness_param": "brightness_first_end"
      },
      {
        "key": "second_start",
        "label": "Second palette start",
        "type": "color",
        "default": "#8375db",
        "random_param": "random_second_start",
        "brightness_param": "brightness_second_start"
      },
      {
        "key": "second_end",
        "label": "Second palette end",
        "type": "color",
        "default": "#64d2c8",
        "random_param": "random_second_end",
        "brightness_param": "brightness_second_end"
      },
      {
        "key": "speed",
        "label": "Speed",
        "type": "float",
        "default": 0.06,
        "min": 0.01,
        "max": 1.0,
        "step": 0.01,
        "unit": "cycles/s"
      },
      {
        "key": "drift",
        "label": "Gradient drift",
        "type": "float",
        "default": 0.25,
        "min": 0.0,
        "max": 1.0,
        "step": 0.05
      },
      {
        "key": "random_first_start",
        "label": "Randomize first palette start",
        "type": "bool",
        "default": false,
        "randomizes": "first_start"
      },
      {
        "key": "random_first_end",
        "label": "Randomize first palette end",
        "type": "bool",
        "default": false,
        "randomizes": "first_end"
      },
      {
        "key": "random_second_start",
        "label": "Randomize second palette start",
        "type": "bool",
        "default": false,
        "randomizes": "second_start"
      },
      {
        "key": "random_second_end",
        "label": "Randomize second palette end",
        "type": "bool",
        "default": false,
        "randomizes": "second_end"
      },
      {
        "key": "brightness_first_start",
        "label": "First palette start brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "first_start"
      },
      {
        "key": "brightness_first_end",
        "label": "First palette end brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "first_end"
      },
      {
        "key": "brightness_second_start",
        "label": "Second palette start brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "second_start"
      },
      {
        "key": "brightness_second_end",
        "label": "Second palette end brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "second_end"
      },
      {
        "key": "color_transition",
        "label": "Color transition",
        "type": "float",
        "default": 8.0,
        "min": 1.0,
        "max": 30.0,
        "step": 0.5,
        "unit": "s"
      }
    ],
    "id": "palette_morph",
    "name": "Palette Morph",
    "description": "A complete gradient transforms into another palette."
  },
  {
    "category": "animated",
    "params": [
      {
        "key": "cool_color",
        "label": "Cooling color",
        "type": "color",
        "default": "#85251d",
        "random_param": "random_cool_color",
        "brightness_param": "brightness_cool_color"
      },
      {
        "key": "hot_color",
        "label": "Hot color",
        "type": "color",
        "default": "#ffb85c",
        "random_param": "random_hot_color",
        "brightness_param": "brightness_hot_color"
      },
      {
        "key": "speed",
        "label": "Speed",
        "type": "float",
        "default": 0.18,
        "min": 0.03,
        "max": 2.0,
        "step": 0.01,
        "unit": "cycles/s"
      },
      {
        "key": "depth",
        "label": "Glow depth",
        "type": "float",
        "default": 0.65,
        "min": 0.1,
        "max": 1.0,
        "step": 0.05
      },
      {
        "key": "random_cool_color",
        "label": "Randomize cooling color",
        "type": "bool",
        "default": false,
        "randomizes": "cool_color"
      },
      {
        "key": "random_hot_color",
        "label": "Randomize hot color",
        "type": "bool",
        "default": false,
        "randomizes": "hot_color"
      },
      {
        "key": "brightness_cool_color",
        "label": "Cooling color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "cool_color"
      },
      {
        "key": "brightness_hot_color",
        "label": "Hot color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "hot_color"
      },
      {
        "key": "color_transition",
        "label": "Color transition",
        "type": "float",
        "default": 8.0,
        "min": 1.0,
        "max": 30.0,
        "step": 0.5,
        "unit": "s"
      }
    ],
    "id": "ember_bed",
    "name": "Ember Bed",
    "description": "Diffuse warm hotspots glow and cool at different rates."
  },
  {
    "category": "animated",
    "params": [
      {
        "key": "color_a",
        "label": "First light",
        "type": "color",
        "default": "#dfed7a",
        "random_param": "random_color_a",
        "brightness_param": "brightness_color_a"
      },
      {
        "key": "color_b",
        "label": "Second light",
        "type": "color",
        "default": "#70e5b3",
        "random_param": "random_color_b",
        "brightness_param": "brightness_color_b"
      },
      {
        "key": "background",
        "label": "Background",
        "type": "color",
        "default": "#071b18",
        "random_param": "random_background",
        "brightness_param": "brightness_background"
      },
      {
        "key": "speed",
        "label": "Speed",
        "type": "float",
        "default": 0.15,
        "min": 0.03,
        "max": 1.5,
        "step": 0.01,
        "unit": "cycles/s"
      },
      {
        "key": "count",
        "label": "Lights",
        "type": "int",
        "default": 3,
        "min": 1,
        "max": 4,
        "step": 1
      },
      {
        "key": "width",
        "label": "Glow width",
        "type": "float",
        "default": 1.75,
        "min": 1.25,
        "max": 5.0,
        "step": 0.25,
        "unit": "ports"
      },
      {
        "key": "random_color_a",
        "label": "Randomize first light",
        "type": "bool",
        "default": false,
        "randomizes": "color_a"
      },
      {
        "key": "random_color_b",
        "label": "Randomize second light",
        "type": "bool",
        "default": false,
        "randomizes": "color_b"
      },
      {
        "key": "random_background",
        "label": "Randomize background",
        "type": "bool",
        "default": false,
        "randomizes": "background"
      },
      {
        "key": "brightness_color_a",
        "label": "First light brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color_a"
      },
      {
        "key": "brightness_color_b",
        "label": "Second light brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "color_b"
      },
      {
        "key": "brightness_background",
        "label": "Background brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "background"
      },
      {
        "key": "color_transition",
        "label": "Color transition",
        "type": "float",
        "default": 8.0,
        "min": 1.0,
        "max": 30.0,
        "step": 0.5,
        "unit": "s"
      }
    ],
    "id": "fireflies",
    "name": "Fireflies",
    "description": "Small lights wander between ports while gently pulsing."
  },
  {
    "category": "data",
    "params": [
      {
        "key": "idle_color",
        "label": "Idle color",
        "type": "color",
        "default": "#386fbd",
        "random_param": "random_idle_color",
        "brightness_param": "brightness_idle_color"
      },
      {
        "key": "busy_color",
        "label": "Busy color",
        "type": "color",
        "default": "#75edc1",
        "random_param": "random_busy_color",
        "brightness_param": "brightness_busy_color"
      },
      {
        "key": "offline_color",
        "label": "No link",
        "type": "color",
        "default": "#000000",
        "random_param": "random_offline_color",
        "brightness_param": "brightness_offline_color"
      },
      {
        "key": "speed",
        "label": "Speed",
        "type": "float",
        "default": 0.2,
        "min": 0.03,
        "max": 2.0,
        "step": 0.01,
        "unit": "cycles/s"
      },
      {
        "key": "width",
        "label": "Trail width",
        "type": "float",
        "default": 2.0,
        "min": 1.25,
        "max": 8.0,
        "step": 0.25,
        "unit": "ports"
      },
      {
        "key": "max_mbps",
        "label": "Full scale",
        "type": "float",
        "default": 1000.0,
        "min": 1.0,
        "max": 10000.0,
        "step": 1.0,
        "unit": "Mbps"
      },
      {
        "key": "random_idle_color",
        "label": "Randomize idle color",
        "type": "bool",
        "default": false,
        "randomizes": "idle_color"
      },
      {
        "key": "random_busy_color",
        "label": "Randomize busy color",
        "type": "bool",
        "default": false,
        "randomizes": "busy_color"
      },
      {
        "key": "random_offline_color",
        "label": "Randomize no link",
        "type": "bool",
        "default": false,
        "randomizes": "offline_color"
      },
      {
        "key": "brightness_idle_color",
        "label": "Idle color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "idle_color"
      },
      {
        "key": "brightness_busy_color",
        "label": "Busy color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "busy_color"
      },
      {
        "key": "brightness_offline_color",
        "label": "No link brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "offline_color"
      },
      {
        "key": "color_transition",
        "label": "Color transition",
        "type": "float",
        "default": 8.0,
        "min": 1.0,
        "max": 30.0,
        "step": 0.5,
        "unit": "s"
      }
    ],
    "id": "traffic_trails",
    "name": "Traffic Trails",
    "description": "Moving trails brighten and change color with each port's traffic."
  },
  {
    "category": "data",
    "params": [
      {
        "key": "active_color",
        "label": "Active color",
        "type": "color",
        "default": "#72e0bd",
        "random_param": "random_active_color",
        "brightness_param": "brightness_active_color"
      },
      {
        "key": "idle_color",
        "label": "Idle color",
        "type": "color",
        "default": "#0b1523",
        "random_param": "random_idle_color",
        "brightness_param": "brightness_idle_color"
      },
      {
        "key": "minimum_mbps",
        "label": "Full glow threshold",
        "type": "float",
        "default": 1.0,
        "min": 0.1,
        "max": 10000.0,
        "step": 0.1,
        "unit": "Mbps",
        "description": "The busiest device reaches full glow at this rate. Above it, other ports scale relative to that device."
      },
      {
        "key": "speed",
        "label": "Fade speed",
        "type": "float",
        "default": 1.0,
        "min": 0.25,
        "max": 4.0,
        "step": 0.05,
        "unit": "x"
      },
      {
        "key": "random_active_color",
        "label": "Randomize active color",
        "type": "bool",
        "default": false,
        "randomizes": "active_color"
      },
      {
        "key": "random_idle_color",
        "label": "Randomize idle color",
        "type": "bool",
        "default": false,
        "randomizes": "idle_color"
      },
      {
        "key": "brightness_active_color",
        "label": "Active color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "active_color"
      },
      {
        "key": "brightness_idle_color",
        "label": "Idle color brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "idle_color"
      },
      {
        "key": "color_transition",
        "label": "Color transition",
        "type": "float",
        "default": 8.0,
        "min": 1.0,
        "max": 30.0,
        "step": 0.5,
        "unit": "s"
      }
    ],
    "id": "activity_glow",
    "name": "Activity Glow",
    "description": "Busier device ports glow brighter and gently settle as traffic falls."
  },
  {
    "category": "data",
    "params": [
      {
        "key": "low_color",
        "label": "Low power",
        "type": "color",
        "default": "#4da6ff",
        "random_param": "random_low_color",
        "brightness_param": "brightness_low_color"
      },
      {
        "key": "high_color",
        "label": "High power",
        "type": "color",
        "default": "#ef78c7",
        "random_param": "random_high_color",
        "brightness_param": "brightness_high_color"
      },
      {
        "key": "no_power_color",
        "label": "No power",
        "type": "color",
        "default": "#111827",
        "random_param": "random_no_power_color",
        "brightness_param": "brightness_no_power_color"
      },
      {
        "key": "speed",
        "label": "Fade speed",
        "type": "float",
        "default": 1.0,
        "min": 0.1,
        "max": 5.0,
        "step": 0.05,
        "unit": "x"
      },
      {
        "key": "random_low_color",
        "label": "Randomize low power",
        "type": "bool",
        "default": false,
        "randomizes": "low_color"
      },
      {
        "key": "random_high_color",
        "label": "Randomize high power",
        "type": "bool",
        "default": false,
        "randomizes": "high_color"
      },
      {
        "key": "random_no_power_color",
        "label": "Randomize no power",
        "type": "bool",
        "default": false,
        "randomizes": "no_power_color"
      },
      {
        "key": "brightness_low_color",
        "label": "Low power brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "low_color"
      },
      {
        "key": "brightness_high_color",
        "label": "High power brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "high_color"
      },
      {
        "key": "brightness_no_power_color",
        "label": "No power brightness",
        "type": "float",
        "default": 100.0,
        "min": 0.0,
        "max": 100.0,
        "step": 1.0,
        "unit": "%",
        "dims": "no_power_color"
      },
      {
        "key": "color_transition",
        "label": "Color transition",
        "type": "float",
        "default": 8.0,
        "min": 1.0,
        "max": 30.0,
        "step": 0.5,
        "unit": "s"
      }
    ],
    "id": "poe_usage",
    "name": "PoE Usage",
    "description": "Blend from low to high color as each port supplies more PoE power."
  }
];
