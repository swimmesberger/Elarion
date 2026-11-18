// Templates for the Elarion.Typst tests. The assertions make a compilation fail when the data, the assets or the
// inputs did not arrive, so a produced PDF proves they did.

#let invoice(data, assets: (:)) = {
  assert.eq(data.number, "2026-0042")
  assert.eq(data.items.len(), 2)
  assert("logo.png" in assets, message: "logo asset missing")
  assert.eq(sys.inputs.at("lang", default: none), "de")
  set document(title: "Invoice " + data.number)
  set page(paper: "a5")
  [= Invoice #data.number]
  for item in data.items [- #item.name: #item.amount \ ]
  image(assets.at("logo.png"), width: 1cm)
}

#let plain(data, assets: (:)) = {
  assert.eq(data, none)
  assert.eq(assets, (:))
  set document(title: "Plain")
  [Hello from Elarion.]
}

#let broken(data, assets: (:)) = {
  panic("the template is broken")
}

#let arguments(data, kind: none, chrome: true, copies: 1, total: none, assets: (:)) = {
  assert.eq(kind, "quote \" and \\ backslash\nnext line")
  assert.eq(chrome, false)
  assert.eq(copies, 3)
  assert.eq(type(total), decimal)
  assert.eq(total, decimal("1280.50"))
  [Arguments arrived.]
}
