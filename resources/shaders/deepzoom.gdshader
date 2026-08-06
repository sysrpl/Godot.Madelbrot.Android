shader_type canvas_item;

// Perturbation-based deep zoom, used only past PERTURBATION_ZOOM_THRESHOLD - see why below.
//
// The regular mandelbrot.gdshader computes an absolute complex coordinate per pixel
// (uv * scale + pan_offset) entirely in float32, which only has ~7 decimal digits of
// precision - somewhere around 10^5-10^6 zoom, pan_offset and its neighboring pixels'
// coordinates collapse to the same float32 value and the image degrades into blocky noise.
//
// Past the threshold, this shader never represents that absolute coordinate in float32 at all.
// Instead, Main.cs (see ComputeReferenceOrbitTexture, gated behind the USE_DEEP_ZOOM #define)
// computes one reference orbit in C# double precision, centered exactly on the current pan
// offset, and uploads it here as ref_orbit_tex. Each pixel then only ever iterates a small
// delta (dz) from that reference:
//
//   z_n = Zn + dz_n                     (Zn = the precomputed reference orbit, dz_n = ours)
//   z_(n+1) = z_n^2 + c = (Zn + dz_n)^2 + (C_ref + dc)
//   Zn+1 = Zn^2 + C_ref                 (that's just the reference orbit's own recurrence)
//   => dz_(n+1) = 2*Zn*dz_n + dz_n^2 + dc
//
// dc (this pixel's offset from the view center, in complex-plane units) and dz both stay small
// once zoomed in far enough - that's what keeps this well-conditioned in float32 where the
// direct/absolute approach would already have collapsed.
//
// WHY THE THRESHOLD EXISTS: dc's magnitude is the visible view's own half-width (roughly
// 1.5 / zoom), not something that shrinks just because this shader is active. At low zoom that
// half-width is a large fraction of the whole Mandelbrot set (dc ~0.25 at zoom 6, for
// instance) - nowhere close to "small", so dz stops tracking the true orbit's delta from the
// reference accurately and the image comes out wrong, often dramatically so. Perturbation only
// pays off (and is only numerically valid) once the view is small enough that every visible
// pixel's dc is small relative to the reference orbit's own values - in practice, well past
// where plain float32 direct iteration is still perfectly fine on its own. So this shader runs
// the exact same direct iteration as mandelbrot.gdshader below the threshold, and only switches
// to the reference-orbit delta iteration above it, right where direct float32 actually needs
// the help.
//
// This pushes the usable zoom depth out to roughly whatever C#'s double precision can resolve
// for the reference orbit (~10^13-10^14), not infinitely deep, and at no extra per-pixel GPU
// cost beyond a texture lookup and the same couple of vec2 multiplies the direct version
// already does. It's a simplification of "real" perturbation-theory renderers, which also
// detect and rebase pixels whose orbit diverges too far from the reference ("glitches") - this
// one doesn't, so very rare pixels far from the reference orbit can render incorrectly at
// extreme zoom. Good enough for pushing the zoom ceiling out; not a from-scratch fractal
// renderer.
const int MAX_ITER = 200;
// Confirmed empirically (not just estimated from float32's ~7 decimal digits) that the direct
// method alone stays glitch-free to at least 35,000x zoom - set comfortably past that so the
// single-reference-orbit glitching (see the header above) has as little room to bite as
// possible, without giving up on perturbation entirely.
const float PERTURBATION_ZOOM_THRESHOLD = 200000.0;

uniform vec2 pan_offset = vec2(-0.5, 0.0);
// The reference orbit as an RG-float texture (width MAX_ITER, height 1; Zn.x/Zn.y in the
// r/g channels) rather than a fixed-size array uniform - texelFetch gives an exact, unfiltered
// per-index lookup, and Texture2D uniforms are a far more reliably-supported upload path from
// C# than array uniforms. See Main.cs's ComputeReferenceOrbitTexture.
uniform sampler2D ref_orbit_tex : filter_nearest, repeat_disable;
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

vec2 complexMul(vec2 a, vec2 b) {
	return vec2(a.x * b.x - a.y * b.y, a.x * b.y + a.y * b.x);
}

void fragment() {
	float aspect = SCREEN_PIXEL_SIZE.y / SCREEN_PIXEL_SIZE.x;
	vec2 uv = UV - vec2(0.5);
	uv.x *= aspect;

	float s = sin(rotation);
	float co = cos(rotation);
	uv = mat2(vec2(co, -s), vec2(s, co)) * uv;

	// This pixel's offset from the view center, in complex-plane units - used as an absolute
	// coordinate's offset below the threshold, or as dc (offset from the reference orbit)
	// above it. Either way this quantity itself is always well-conditioned; see the file header
	// for why it's specifically pan_offset that isn't, once zoomed in deep enough.
	vec2 dc = uv * (3.0 / zoom);

	vec2 z = vec2(0.0);
	int iterations = MAX_ITER;

	if (zoom < PERTURBATION_ZOOM_THRESHOLD) {
		// Shallow enough that plain float32 direct iteration is perfectly adequate - same
		// computation as mandelbrot.gdshader.
		vec2 c = pan_offset + dc;
		for (int i = 0; i < MAX_ITER; i++) {
			if (dot(z, z) > 4.0) {
				iterations = i;
				break;
			}
			z = complexMul(z, z) + c;
		}
	} else {
		vec2 dz = vec2(0.0);
		for (int i = 0; i < MAX_ITER; i++) {
			vec2 Zn = texelFetch(ref_orbit_tex, ivec2(i, 0), 0).rg;
			z = Zn + dz;
			if (dot(z, z) > 4.0) {
				iterations = i;
				break;
			}
			// dz_(n+1) = 2*Zn*dz + dz^2 + dc, factored into one complex multiply instead of
			// two: (2*Zn + dz) * dz == 2*Zn*dz + dz*dz.
			dz = complexMul(2.0 * Zn + dz, dz) + dc;
		}
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
