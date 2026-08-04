shader_type canvas_item;

const int MAX_ITER = 200;

uniform vec2 pan_offset = vec2(-0.5, 0.0);
uniform float zoom = 1.0;
uniform float rotation = 0.0;

// Palette lifted from docs/pallete.webp (a classic navy/blue/cream/gold/orange/maroon
// cyclic gradient), sampled and reduced to these stops. Cycles smoothly back to the
// first color after the last one.
const int PALETTE_COUNT = 7;
const vec3 PALETTE[PALETTE_COUNT] = vec3[](
	vec3(0.05, 0.11, 0.44), // deep navy
	vec3(0.27, 0.55, 0.76), // blue
	vec3(0.60, 0.83, 0.93), // light blue / cyan
	vec3(0.96, 0.95, 0.85), // cream / white highlight
	vec3(0.97, 0.80, 0.20), // gold
	vec3(0.89, 0.52, 0.20), // orange
	vec3(0.52, 0.22, 0.13)  // rust / maroon, blends back into navy
);

vec3 palette(float t) {
	float scaled = fract(t) * float(PALETTE_COUNT);
	int i0 = int(scaled) % PALETTE_COUNT;
	int i1 = (i0 + 1) % PALETTE_COUNT;
	// smoothstep instead of a raw linear factor: each segment eases in/out with zero
	// slope at both ends, so the rate of change matches up across stop boundaries instead
	// of kinking there (which the eye reads as a faint band even though the color itself
	// is already continuous).
	float blend = smoothstep(0.0, 1.0, fract(scaled));
	return mix(PALETTE[i0], PALETTE[i1], blend);
}

void fragment() {
	float aspect = SCREEN_PIXEL_SIZE.y / SCREEN_PIXEL_SIZE.x;
	vec2 uv = UV - vec2(0.5);
	uv.x *= aspect;

	float s = sin(rotation);
	float co = cos(rotation);
	uv = mat2(vec2(co, -s), vec2(s, co)) * uv;

	vec2 c = uv * (3.0 / zoom) + pan_offset;

	vec2 z = vec2(0.0);
	int iterations = MAX_ITER;
	for (int i = 0; i < MAX_ITER; i++) {
		if (dot(z, z) > 4.0) {
			iterations = i;
			break;
		}
		z = vec2(z.x * z.x - z.y * z.y, 2.0 * z.x * z.y) + c;
	}

	vec3 color = vec3(0.0);
	if (iterations < MAX_ITER) {
		// Smooth (continuous) escape count instead of the raw integer iterations,
		// so the color ramp doesn't band at integer boundaries.
		float smoothIter = float(iterations) + 1.0 - log2(log2(dot(z, z)));
		float t = smoothIter / float(MAX_ITER);
		color = palette(t * 3.0);
	}

	COLOR = vec4(color, 1.0);
}
