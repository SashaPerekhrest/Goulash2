import { useParams } from 'react-router-dom'
import { PageScaffold } from '../shared/PageScaffold'

export function SupplierDetailsPage() {
  const { id } = useParams()
  return <PageScaffold eyebrow="ПОСТАВЩИК" title="Детали поставщика"
    description={`Карточка ${id ?? ''} появится после реализации API каталога.`} />
}
