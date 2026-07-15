import { source } from '@/lib/source';
import { createFromSource } from 'fumadocs-core/search/server';

export const revalidate = false;

export const { staticGET: GET } = createFromSource(source, {
  // https://docs.orama.com/docs/orama-js/supported-languages
  language: 'english',
  // Static Orama indexes multiply every indexed token into several lookup structures. Keep every
  // page title and heading, but bound body prose by sampling each structured section. This preserves
  // broad full-text discovery while avoiding a multi-megabyte index dominated by long reference pages.
  buildIndex: async (page) => {
    const structuredData = page.data.structuredData;

    if (!structuredData) {
      throw new Error(`Missing structured search data for ${page.url}`);
    }

    const bodyBudget = 5_000;
    const perSection = Math.max(40, Math.floor(bodyBudget / Math.max(1, structuredData.contents.length)));

    return {
      id: page.url,
      title: page.data.title ?? page.url,
      description: page.data.description,
      url: page.url,
      structuredData: {
        headings: structuredData.headings,
        contents: structuredData.contents.map((section) => ({
          ...section,
          content: section.content.slice(0, perSection),
        })),
      },
    };
  },
});
