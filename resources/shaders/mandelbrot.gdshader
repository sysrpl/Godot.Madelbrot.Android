shader_type canvas_item;

const int MAX_ITER = 200;

uniform vec2 pan_offset = vec2(-0.5, 0.0);
uniform float zoom = 1.0;
uniform float rotation = 0.0;

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
		color = 0.5 + 0.5 * cos(6.2831853 * (t * 3.0 + vec3(0.0, 0.33, 0.67)));
	}

	COLOR = vec4(color, 1.0);
}
