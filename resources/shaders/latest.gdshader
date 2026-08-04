shader_type canvas_item;

const int MAX_ITER = 200;

// Pan center, each axis split into a (hi, lo) float pair emulating ~46 bits of mantissa
// instead of float32's 23 bits, so the iteration stays accurate far deeper into the zoom
// than plain float32 allows before catastrophic cancellation turns the image to noise.
uniform vec2 pan_offset_x = vec2(-0.5, 0.0);
uniform vec2 pan_offset_y = vec2(0.0, 0.0);
uniform float zoom = 1.0;
uniform float rotation = 0.0;

// -- Double-float (df64) helpers: a real number is represented as vec2(hi, lo) with
// true value == hi + lo. Based on Dekker's algorithm for error-compensated float math.

// Veltkamp split, used by twoProd to multiply two floats without losing the rounding error.
vec2 split(float a) {
	float t = a * 4097.0; // 2^12 + 1, exact for float32's 24-bit significand.
	float hi = t - (t - a);
	float lo = a - hi;
	return vec2(hi, lo);
}

// Exact product of two floats as a (hi, lo) pair.
vec2 twoProd(float a, float b) {
	float p = a * b;
	vec2 aS = split(a);
	vec2 bS = split(b);
	float err = ((aS.x * bS.x - p) + aS.x * bS.y + aS.y * bS.x) + aS.y * bS.y;
	return vec2(p, err);
}

// Exact sum of two floats as a (hi, lo) pair.
vec2 twoSum(float a, float b) {
	float s = a + b;
	float v = s - a;
	float e = (a - (s - v)) + (b - v);
	return vec2(s, e);
}

// Sum of two floats where |a| >= |b|; cheaper than twoSum.
vec2 quickTwoSum(float a, float b) {
	float s = a + b;
	float e = b - (s - a);
	return vec2(s, e);
}

vec2 dfAdd(vec2 a, vec2 b) {
	vec2 s = twoSum(a.x, b.x);
	s.y += a.y + b.y;
	return quickTwoSum(s.x, s.y);
}

vec2 dfSub(vec2 a, vec2 b) {
	return dfAdd(a, vec2(-b.x, -b.y));
}

vec2 dfMul(vec2 a, vec2 b) {
	vec2 p = twoProd(a.x, b.x);
	p.y += a.x * b.y + a.y * b.x;
	return quickTwoSum(p.x, p.y);
}

void fragment() {
	float aspect = SCREEN_PIXEL_SIZE.y / SCREEN_PIXEL_SIZE.x;
	vec2 uv = UV - vec2(0.5);
	uv.x *= aspect;

	float s = sin(rotation);
	float co = cos(rotation);
	uv = mat2(vec2(co, -s), vec2(s, co)) * uv;

	float scale = 3.0 / zoom;

	// Complex coordinate for this pixel, computed as pan_offset + (exact) uv * scale,
	// so the multiply doesn't introduce rounding error on top of the high-precision pan.
	vec2 cx = dfAdd(pan_offset_x, twoProd(uv.x, scale));
	vec2 cy = dfAdd(pan_offset_y, twoProd(uv.y, scale));

	vec2 zx = vec2(0.0);
	vec2 zy = vec2(0.0);
	int iterations = MAX_ITER;
	for (int i = 0; i < MAX_ITER; i++) {
		if (zx.x * zx.x + zy.x * zy.x > 4.0) {
			iterations = i;
			break;
		}
		vec2 zx2 = dfMul(zx, zx);
		vec2 zy2 = dfMul(zy, zy);
		vec2 zxzy = dfMul(zx, zy);
		vec2 newZx = dfAdd(dfSub(zx2, zy2), cx);
		vec2 newZy = dfAdd(dfAdd(zxzy, zxzy), cy);
		zx = newZx;
		zy = newZy;
	}

	vec3 color = vec3(0.0);
	if (iterations < MAX_ITER) {
		// Smooth (continuous) escape count instead of the raw integer iterations,
		// so the color ramp doesn't band at integer boundaries.
		float magnitudeSq = zx.x * zx.x + zy.x * zy.x;
		float smoothIter = float(iterations) + 1.0 - log2(log2(magnitudeSq));
		float t = smoothIter / float(MAX_ITER);
		color = 0.5 + 0.5 * cos(6.2831853 * (t * 3.0 + vec3(0.0, 0.33, 0.67)));
	}

	COLOR = vec4(color, 1.0);
}
